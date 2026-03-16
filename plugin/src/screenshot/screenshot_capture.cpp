#include "screenshot_capture.h"

#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_4.h>
#include <wincodec.h>
#include <objbase.h>
#include <nlohmann/json.hpp>
#include <vector>
#include <mutex>
#include <condition_variable>
#include <atomic>
#include <string>
#include <chrono>
#include <cstring>
#include <cstdint>
#include <algorithm>

using json = nlohmann::json;

// ---------- Box-filter downscale (averages source pixels per dest pixel) ----------

static std::vector<uint8_t> downscale_box(const uint8_t* src, int src_w, int src_h, int dst_w, int dst_h) {
    std::vector<uint8_t> dst(dst_w * dst_h * 4);
    for (int y = 0; y < dst_h; ++y) {
        int sy0 = y * src_h / dst_h;
        int sy1 = (y + 1) * src_h / dst_h;
        if (sy1 <= sy0) sy1 = sy0 + 1;
        if (sy1 > src_h) sy1 = src_h;
        for (int x = 0; x < dst_w; ++x) {
            int sx0 = x * src_w / dst_w;
            int sx1 = (x + 1) * src_w / dst_w;
            if (sx1 <= sx0) sx1 = sx0 + 1;
            if (sx1 > src_w) sx1 = src_w;
            // Average all source pixels in [sx0,sx1) x [sy0,sy1)
            uint32_t rb = 0, gb = 0, bb = 0, ab = 0, count = 0;
            for (int sy = sy0; sy < sy1; ++sy) {
                for (int sx = sx0; sx < sx1; ++sx) {
                    const uint8_t* sp = src + (sy * src_w + sx) * 4;
                    bb += sp[0]; gb += sp[1]; rb += sp[2]; ab += sp[3];
                    ++count;
                }
            }
            uint8_t* dp = dst.data() + (y * dst_w + x) * 4;
            dp[0] = (uint8_t)(bb / count);
            dp[1] = (uint8_t)(gb / count);
            dp[2] = (uint8_t)(rb / count);
            dp[3] = (uint8_t)(ab / count);
        }
    }
    return dst;
}

// ---------- JPEG encoding via WIC (Windows Imaging Component) ----------

static std::vector<uint8_t> encode_jpeg(const uint8_t* bgra, int w, int h, int quality) {
    std::vector<uint8_t> result;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    IWICImagingFactory* factory = nullptr;
    HRESULT hr = CoCreateInstance(
        CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&factory));
    if (FAILED(hr) || !factory) return result;

    IStream* stream = nullptr;
    hr = CreateStreamOnHGlobal(nullptr, TRUE, &stream);
    if (FAILED(hr) || !stream) { factory->Release(); return result; }

    IWICBitmapEncoder* encoder = nullptr;
    hr = factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr, &encoder);
    if (FAILED(hr) || !encoder) { stream->Release(); factory->Release(); return result; }

    encoder->Initialize(stream, WICBitmapEncoderNoCache);

    IWICBitmapFrameEncode* frame = nullptr;
    IPropertyBag2* props = nullptr;
    encoder->CreateNewFrame(&frame, &props);

    // Set JPEG quality (0.0 to 1.0)
    if (props) {
        PROPBAG2 option{};
        wchar_t name[] = L"ImageQuality";
        option.pstrName = name;
        VARIANT val;
        VariantInit(&val);
        val.vt = VT_R4;
        val.fltVal = static_cast<float>(quality) / 100.0f;
        props->Write(1, &option, &val);
    }

    frame->Initialize(props);
    frame->SetSize(w, h);

    WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppBGRA;
    frame->SetPixelFormat(&fmt);
    frame->WritePixels(h, w * 4, w * h * 4, const_cast<BYTE*>(bgra));
    frame->Commit();
    encoder->Commit();

    // Read encoded bytes from stream
    STATSTG stat{};
    stream->Stat(&stat, STATFLAG_NONAME);
    LARGE_INTEGER zero{};
    stream->Seek(zero, STREAM_SEEK_SET, nullptr);
    result.resize(stat.cbSize.LowPart);
    ULONG bytes_read = 0;
    stream->Read(result.data(), static_cast<ULONG>(result.size()), &bytes_read);
    result.resize(bytes_read);

    if (frame) frame->Release();
    if (props) props->Release();
    encoder->Release();
    stream->Release();
    factory->Release();
    return result;
}

// ---------- Base64 encoding ----------

static std::string base64_encode(const std::vector<uint8_t>& data) {
    static const char table[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    std::string result;
    size_t len = data.size();
    result.reserve(((len + 2) / 3) * 4);

    for (size_t i = 0; i < len; i += 3) {
        uint32_t octet_a = data[i];
        uint32_t octet_b = (i + 1 < len) ? data[i + 1] : 0;
        uint32_t octet_c = (i + 2 < len) ? data[i + 2] : 0;

        uint32_t triple = (octet_a << 16) | (octet_b << 8) | octet_c;

        result += table[(triple >> 18) & 0x3F];
        result += table[(triple >> 12) & 0x3F];
        result += (i + 1 < len) ? table[(triple >> 6) & 0x3F] : '=';
        result += (i + 2 < len) ? table[triple & 0x3F] : '=';
    }

    return result;
}

// ---------- ScreenshotCapture implementation ----------

ScreenshotCapture& ScreenshotCapture::get() {
    static ScreenshotCapture instance;
    return instance;
}

void ScreenshotCapture::initialize(void* device, void* swap_chain, void* command_queue, int renderer_type) {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_device = device;
    m_swap_chain = swap_chain;
    m_command_queue = command_queue;
    m_renderer_type = renderer_type;
    m_initialized = true;
}

json ScreenshotCapture::capture(int max_width, int max_height, int quality, int timeout_ms) {
    if (!m_initialized) {
        return json{{"error", "Screenshot capture not initialized. Call initialize() first."}};
    }

    // UEVR_RENDERER_D3D11 = 0, UEVR_RENDERER_D3D12 = 1
    if (m_renderer_type != 0 && m_renderer_type != 1) {
        return json{{"error", "Unknown renderer type: " + std::to_string(m_renderer_type)}};
    }

    // Set up the request
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_requested_max_width = max_width;
        m_requested_max_height = max_height;
        m_result_ready = false;
        m_result_error.clear();
        m_result_bmp.clear();
        m_result_width = 0;
        m_result_height = 0;
    }

    // Signal that a capture is requested
    m_capture_requested.store(true);

    // Wait for the present thread to complete the capture
    {
        std::unique_lock<std::mutex> lock(m_mutex);
        bool ok = m_cv.wait_for(lock, std::chrono::milliseconds(timeout_ms), [this]() {
            return m_result_ready || !m_result_error.empty();
        });

        if (!ok) {
            m_capture_requested.store(false);
            return json{{"error", "Screenshot capture timeout after " + std::to_string(timeout_ms) + "ms — no present() call received"}};
        }

        if (!m_result_error.empty()) {
            return json{{"error", m_result_error}};
        }
    }

    // Determine output dimensions
    int out_w = m_result_width;
    int out_h = m_result_height;
    if (max_width > 0 && out_w > max_width) {
        double scale = (double)max_width / (double)out_w;
        out_w = max_width;
        out_h = (int)(m_result_height * scale);
        if (out_h < 1) out_h = 1;
    }
    if (max_height > 0 && out_h > max_height) {
        double scale = (double)max_height / (double)out_h;
        out_h = max_height;
        out_w = (int)(out_w * scale);
        if (out_w < 1) out_w = 1;
    }

    // Use WIC for scaling + JPEG encoding (high-quality Fant interpolation)
    if (quality < 1) quality = 1;
    if (quality > 100) quality = 100;

    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IWICImagingFactory* factory = nullptr;
    HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
    if (FAILED(hr) || !factory) return json{{"error", "WIC factory creation failed"}};

    // Create bitmap from raw pixels
    IWICBitmap* bitmap = nullptr;
    hr = factory->CreateBitmapFromMemory(m_result_width, m_result_height, GUID_WICPixelFormat32bppBGRA,
                                          m_result_width * 4, (UINT)(m_result_width * m_result_height * 4),
                                          m_result_bmp.data(), &bitmap);
    if (FAILED(hr) || !bitmap) { factory->Release(); return json{{"error", "WIC bitmap creation failed"}}; }

    // Scale if needed
    IWICBitmapSource* source = bitmap;
    IWICBitmapScaler* scaler = nullptr;
    if (out_w != m_result_width || out_h != m_result_height) {
        factory->CreateBitmapScaler(&scaler);
        scaler->Initialize(bitmap, out_w, out_h, WICBitmapInterpolationModeFant);
        source = scaler;
    }

    // Encode to JPEG
    IStream* stream = nullptr;
    CreateStreamOnHGlobal(nullptr, TRUE, &stream);
    IWICBitmapEncoder* encoder = nullptr;
    factory->CreateEncoder(GUID_ContainerFormatJpeg, nullptr, &encoder);
    encoder->Initialize(stream, WICBitmapEncoderNoCache);

    IWICBitmapFrameEncode* frame = nullptr;
    IPropertyBag2* props = nullptr;
    encoder->CreateNewFrame(&frame, &props);

    if (props) {
        PROPBAG2 option{};
        wchar_t name[] = L"ImageQuality";
        option.pstrName = name;
        VARIANT val;
        VariantInit(&val);
        val.vt = VT_R4;
        val.fltVal = static_cast<float>(quality) / 100.0f;
        props->Write(1, &option, &val);
    }

    frame->Initialize(props);
    frame->SetSize(out_w, out_h);
    WICPixelFormatGUID fmt = GUID_WICPixelFormat32bppBGRA;
    frame->SetPixelFormat(&fmt);
    frame->WriteSource(source, nullptr); // WIC handles format conversion + scaling
    frame->Commit();
    encoder->Commit();

    // Read encoded bytes
    STATSTG stat{};
    stream->Stat(&stat, STATFLAG_NONAME);
    LARGE_INTEGER zero{};
    stream->Seek(zero, STREAM_SEEK_SET, nullptr);
    std::vector<uint8_t> jpeg_data(stat.cbSize.LowPart);
    ULONG bytes_read = 0;
    stream->Read(jpeg_data.data(), (ULONG)jpeg_data.size(), &bytes_read);
    jpeg_data.resize(bytes_read);

    // Cleanup
    if (frame) frame->Release();
    if (props) props->Release();
    encoder->Release();
    stream->Release();
    if (scaler) scaler->Release();
    bitmap->Release();
    factory->Release();

    if (jpeg_data.empty()) return json{{"error", "JPEG encoding failed"}};

    auto b64 = base64_encode(jpeg_data);

    json result;
    result["width"] = out_w;
    result["height"] = out_h;
    result["format"] = "jpeg";
    result["quality"] = quality;
    result["size"] = (int)jpeg_data.size();
    result["sourceWidth"] = m_result_width;
    result["sourceHeight"] = m_result_height;
    result["data"] = b64;
    return result;
}

void ScreenshotCapture::on_present() {
    if (!m_capture_requested.load()) {
        return;
    }

    if (m_renderer_type == 0) { // UEVR_RENDERER_D3D11
        capture_d3d11();
    } else if (m_renderer_type == 1) { // UEVR_RENDERER_D3D12
        capture_d3d12();
    } else {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Unsupported renderer type: " + std::to_string(m_renderer_type);
        m_capture_requested.store(false);
        m_cv.notify_all();
    }
}

void ScreenshotCapture::capture_d3d11() {
    IDXGISwapChain* swapchain = static_cast<IDXGISwapChain*>(m_swap_chain);
    ID3D11Device* device = static_cast<ID3D11Device*>(m_device);

    // Get the back buffer
    ID3D11Texture2D* backbuffer = nullptr;
    HRESULT hr = swapchain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backbuffer);
    if (FAILED(hr) || !backbuffer) {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to get backbuffer from swap chain (HRESULT=0x" +
            ([](HRESULT h) { char buf[16]; snprintf(buf, sizeof(buf), "%08X", (unsigned)h); return std::string(buf); })(hr) + ")";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // Get immediate context
    ID3D11DeviceContext* context = nullptr;
    device->GetImmediateContext(&context);
    if (!context) {
        backbuffer->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to get D3D11 immediate context";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // Get backbuffer description
    D3D11_TEXTURE2D_DESC bb_desc{};
    backbuffer->GetDesc(&bb_desc);

    // Create staging texture for CPU read
    D3D11_TEXTURE2D_DESC staging_desc{};
    staging_desc.Width = bb_desc.Width;
    staging_desc.Height = bb_desc.Height;
    staging_desc.MipLevels = 1;
    staging_desc.ArraySize = 1;
    staging_desc.Format = bb_desc.Format;
    staging_desc.SampleDesc.Count = 1;
    staging_desc.SampleDesc.Quality = 0;
    staging_desc.Usage = D3D11_USAGE_STAGING;
    staging_desc.BindFlags = 0;
    staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    staging_desc.MiscFlags = 0;

    ID3D11Texture2D* staging = nullptr;
    hr = device->CreateTexture2D(&staging_desc, nullptr, &staging);
    if (FAILED(hr) || !staging) {
        context->Release();
        backbuffer->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to create staging texture";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // If the backbuffer is multisampled, we need a resolve step
    if (bb_desc.SampleDesc.Count > 1) {
        // Create a non-multisampled intermediate texture
        D3D11_TEXTURE2D_DESC resolve_desc = bb_desc;
        resolve_desc.SampleDesc.Count = 1;
        resolve_desc.SampleDesc.Quality = 0;
        resolve_desc.Usage = D3D11_USAGE_DEFAULT;
        resolve_desc.BindFlags = 0;
        resolve_desc.CPUAccessFlags = 0;
        resolve_desc.MiscFlags = 0;

        ID3D11Texture2D* resolved = nullptr;
        hr = device->CreateTexture2D(&resolve_desc, nullptr, &resolved);
        if (FAILED(hr) || !resolved) {
            staging->Release();
            context->Release();
            backbuffer->Release();
            std::lock_guard<std::mutex> lock(m_mutex);
            m_result_error = "Failed to create resolve texture for MSAA backbuffer";
            m_capture_requested.store(false);
            m_cv.notify_all();
            return;
        }

        context->ResolveSubresource(resolved, 0, backbuffer, 0, bb_desc.Format);
        context->CopyResource(staging, resolved);
        resolved->Release();
    } else {
        context->CopyResource(staging, backbuffer);
    }

    // Map staging texture
    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) {
        staging->Release();
        context->Release();
        backbuffer->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to map staging texture";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    int src_w = (int)bb_desc.Width;
    int src_h = (int)bb_desc.Height;

    // Read source pixels as BGRA
    // The backbuffer format might be DXGI_FORMAT_R8G8B8A8_UNORM (RGBA) or
    // DXGI_FORMAT_B8G8R8A8_UNORM (BGRA). We need to handle both.
    bool is_rgba = (bb_desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
                    bb_desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                    bb_desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS);

    std::vector<uint8_t> src_bgra(src_w * src_h * 4);
    for (int y = 0; y < src_h; ++y) {
        const uint8_t* src_row = static_cast<const uint8_t*>(mapped.pData) + y * mapped.RowPitch;
        uint8_t* dst_row = src_bgra.data() + y * src_w * 4;
        if (is_rgba) {
            // Convert RGBA -> BGRA
            for (int x = 0; x < src_w; ++x) {
                dst_row[x * 4 + 0] = src_row[x * 4 + 2]; // B
                dst_row[x * 4 + 1] = src_row[x * 4 + 1]; // G
                dst_row[x * 4 + 2] = src_row[x * 4 + 0]; // R
                dst_row[x * 4 + 3] = src_row[x * 4 + 3]; // A
            }
        } else {
            // Already BGRA (or close enough — B8G8R8A8, B8G8R8X8, etc.)
            std::memcpy(dst_row, src_row, src_w * 4);
        }
    }

    context->Unmap(staging, 0);
    staging->Release();
    context->Release();
    backbuffer->Release();

    // Store full-resolution pixels — scaling happens in capture() via WIC
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_bmp = std::move(src_bgra);
        m_result_width = src_w;
        m_result_height = src_h;
        m_result_ready = true;
        m_result_error.clear();
        m_capture_requested.store(false);
    }
    m_cv.notify_all();
}

void ScreenshotCapture::capture_d3d12() {
    ID3D12Device* device = static_cast<ID3D12Device*>(m_device);
    IDXGISwapChain3* swapchain = nullptr;

    // The swap chain from UEVR might be IDXGISwapChain1/3/4 — QI for IDXGISwapChain3
    HRESULT hr = static_cast<IUnknown*>(m_swap_chain)->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&swapchain);
    if (FAILED(hr) || !swapchain) {
        // Try as plain IDXGISwapChain
        IDXGISwapChain* sc1 = static_cast<IDXGISwapChain*>(m_swap_chain);
        hr = sc1->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&swapchain);
        if (FAILED(hr) || !swapchain) {
            std::lock_guard<std::mutex> lock(m_mutex);
            m_result_error = "Failed to get IDXGISwapChain3";
            m_capture_requested.store(false);
            m_cv.notify_all();
            return;
        }
    }

    UINT backbuffer_idx = swapchain->GetCurrentBackBufferIndex();

    ID3D12Resource* backbuffer = nullptr;
    hr = swapchain->GetBuffer(backbuffer_idx, __uuidof(ID3D12Resource), (void**)&backbuffer);
    if (FAILED(hr) || !backbuffer) {
        swapchain->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to get D3D12 backbuffer";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    D3D12_RESOURCE_DESC bb_desc = backbuffer->GetDesc();
    int src_w = (int)bb_desc.Width;
    int src_h = (int)bb_desc.Height;

    // Get the actual GPU footprint for proper row pitch alignment
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
    UINT num_rows = 0;
    UINT64 row_size_bytes = 0;
    UINT64 total_bytes = 0;
    device->GetCopyableFootprints(&bb_desc, 0, 1, 0, &footprint, &num_rows, &row_size_bytes, &total_bytes);

    UINT64 row_pitch = footprint.Footprint.RowPitch;

    // Create a readback buffer (heap)
    D3D12_HEAP_PROPERTIES heap_props{};
    heap_props.Type = D3D12_HEAP_TYPE_READBACK;

    D3D12_RESOURCE_DESC readback_desc{};
    readback_desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    readback_desc.Width = total_bytes;
    readback_desc.Height = 1;
    readback_desc.DepthOrArraySize = 1;
    readback_desc.MipLevels = 1;
    readback_desc.Format = DXGI_FORMAT_UNKNOWN;
    readback_desc.SampleDesc.Count = 1;
    readback_desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

    ID3D12Resource* readback = nullptr;
    hr = device->CreateCommittedResource(&heap_props, D3D12_HEAP_FLAG_NONE, &readback_desc,
                                          D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                          __uuidof(ID3D12Resource), (void**)&readback);
    if (FAILED(hr) || !readback) {
        backbuffer->Release();
        swapchain->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to create D3D12 readback buffer";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // Create a command allocator and command list for the copy
    ID3D12CommandAllocator* alloc = nullptr;
    hr = device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator), (void**)&alloc);
    if (FAILED(hr) || !alloc) {
        readback->Release();
        backbuffer->Release();
        swapchain->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to create D3D12 command allocator";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    ID3D12GraphicsCommandList* cmd = nullptr;
    hr = device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr,
                                    __uuidof(ID3D12GraphicsCommandList), (void**)&cmd);
    if (FAILED(hr) || !cmd) {
        alloc->Release();
        readback->Release();
        backbuffer->Release();
        swapchain->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to create D3D12 command list";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // Transition backbuffer to COPY_SOURCE
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = backbuffer;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    cmd->ResourceBarrier(1, &barrier);

    // Copy backbuffer to readback
    D3D12_TEXTURE_COPY_LOCATION src_loc{};
    src_loc.pResource = backbuffer;
    src_loc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
    src_loc.SubresourceIndex = 0;

    D3D12_TEXTURE_COPY_LOCATION dst_loc{};
    dst_loc.pResource = readback;
    dst_loc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
    dst_loc.PlacedFootprint = footprint;

    cmd->CopyTextureRegion(&dst_loc, 0, 0, 0, &src_loc, nullptr);

    // Transition backbuffer back to PRESENT
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
    cmd->ResourceBarrier(1, &barrier);

    cmd->Close();

    // Use UEVR's command queue (or create one as fallback)
    ID3D12CommandQueue* queue = nullptr;
    bool own_queue = false;
    if (m_command_queue) {
        queue = static_cast<ID3D12CommandQueue*>(m_command_queue);
    } else {
        D3D12_COMMAND_QUEUE_DESC queue_desc{};
        queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        hr = device->CreateCommandQueue(&queue_desc, __uuidof(ID3D12CommandQueue), (void**)&queue);
        own_queue = true;
        if (FAILED(hr) || !queue) {
            cmd->Release();
            alloc->Release();
            readback->Release();
            backbuffer->Release();
            swapchain->Release();
            std::lock_guard<std::mutex> lock(m_mutex);
            m_result_error = "Failed to create D3D12 command queue";
            m_capture_requested.store(false);
            m_cv.notify_all();
            return;
        }
    }

    ID3D12Fence* fence = nullptr;
    hr = device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), (void**)&fence);
    HANDLE fence_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);

    ID3D12CommandList* lists[] = { cmd };
    queue->ExecuteCommandLists(1, lists);
    queue->Signal(fence, 1);
    fence->SetEventOnCompletion(1, fence_event);
    WaitForSingleObject(fence_event, 5000);
    CloseHandle(fence_event);

    // Map readback and read pixels
    void* mapped = nullptr;
    D3D12_RANGE read_range{0, total_bytes};
    hr = readback->Map(0, &read_range, &mapped);
    if (FAILED(hr) || !mapped) {
        fence->Release();
        if (own_queue) queue->Release();
        cmd->Release();
        alloc->Release();
        readback->Release();
        backbuffer->Release();
        swapchain->Release();
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_error = "Failed to map D3D12 readback buffer";
        m_capture_requested.store(false);
        m_cv.notify_all();
        return;
    }

    // Determine pixel format and convert to BGRA8
    bool is_rgba8 = (bb_desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM ||
                     bb_desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                     bb_desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS);
    bool is_bgra8 = (bb_desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM ||
                     bb_desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ||
                     bb_desc.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS ||
                     bb_desc.Format == DXGI_FORMAT_B8G8R8X8_UNORM ||
                     bb_desc.Format == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB);
    // Determine bytes per pixel from format
    int bpp = 4; // default: 4 bytes per pixel
    bool is_r10g10b10a2 = (bb_desc.Format == DXGI_FORMAT_R10G10B10A2_UNORM ||
                           bb_desc.Format == DXGI_FORMAT_R10G10B10A2_TYPELESS);
    bool is_r16g16b16a16_float = (bb_desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT);
    if (is_r16g16b16a16_float) bpp = 8;

    // Heuristic: if format says 4bpp but row_pitch suggests 8bpp, trust the pitch
    // (UEVR can wrap swap chains and the reported format may not match actual data)
    int expected_pitch_4bpp = ((src_w * 4 + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) /
                                D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * D3D12_TEXTURE_DATA_PITCH_ALIGNMENT;
    int expected_pitch_8bpp = ((src_w * 8 + D3D12_TEXTURE_DATA_PITCH_ALIGNMENT - 1) /
                                D3D12_TEXTURE_DATA_PITCH_ALIGNMENT) * D3D12_TEXTURE_DATA_PITCH_ALIGNMENT;
    // Check if the actual row_size_bytes (unpadded) suggests 8bpp
    if (bpp == 4 && row_size_bytes > (UINT64)src_w * 4) {
        // Actual data per row exceeds 4bpp — likely FP16 (8bpp)
        bpp = 8;
        is_r16g16b16a16_float = true;
        is_r10g10b10a2 = false;
    }

    std::vector<uint8_t> src_bgra(src_w * src_h * 4);
    for (int y = 0; y < src_h; ++y) {
        const uint8_t* src_row = static_cast<const uint8_t*>(mapped) + y * row_pitch;
        uint8_t* dst_row = src_bgra.data() + y * src_w * 4;

        if (is_r10g10b10a2) {
            // 10-bit per channel: unpack R10G10B10A2 → BGRA8
            const uint32_t* pixels = reinterpret_cast<const uint32_t*>(src_row);
            for (int x = 0; x < src_w; ++x) {
                uint32_t p = pixels[x];
                uint8_t r = (uint8_t)(((p >>  0) & 0x3FF) >> 2);
                uint8_t g = (uint8_t)(((p >> 10) & 0x3FF) >> 2);
                uint8_t b = (uint8_t)(((p >> 20) & 0x3FF) >> 2);
                dst_row[x * 4 + 0] = b;
                dst_row[x * 4 + 1] = g;
                dst_row[x * 4 + 2] = r;
                dst_row[x * 4 + 3] = 255;
            }
        } else if (is_r16g16b16a16_float) {
            // FP16 HDR: convert to BGRA8 with simple tone mapping
            const uint16_t* pixels = reinterpret_cast<const uint16_t*>(src_row);
            auto half_to_float = [](uint16_t h) -> float {
                uint32_t sign = (h >> 15) & 1;
                uint32_t exp = (h >> 10) & 0x1F;
                uint32_t mant = h & 0x3FF;
                if (exp == 0) return sign ? -0.0f : 0.0f;
                if (exp == 31) return sign ? -1e30f : 1e30f;
                float f = std::ldexp((float)(mant | 0x400), (int)exp - 25);
                return sign ? -f : f;
            };
            for (int x = 0; x < src_w; ++x) {
                float r = half_to_float(pixels[x * 4 + 0]);
                float g = half_to_float(pixels[x * 4 + 1]);
                float b = half_to_float(pixels[x * 4 + 2]);
                // Simple Reinhard tone mapping + gamma
                r = r / (1.0f + r); g = g / (1.0f + g); b = b / (1.0f + b);
                dst_row[x * 4 + 0] = (uint8_t)(b > 1.0f ? 255 : (b < 0 ? 0 : (int)(b * 255.0f)));
                dst_row[x * 4 + 1] = (uint8_t)(g > 1.0f ? 255 : (g < 0 ? 0 : (int)(g * 255.0f)));
                dst_row[x * 4 + 2] = (uint8_t)(r > 1.0f ? 255 : (r < 0 ? 0 : (int)(r * 255.0f)));
                dst_row[x * 4 + 3] = 255;
            }
        } else if (is_rgba8) {
            for (int x = 0; x < src_w; ++x) {
                dst_row[x * 4 + 0] = src_row[x * 4 + 2]; // B
                dst_row[x * 4 + 1] = src_row[x * 4 + 1]; // G
                dst_row[x * 4 + 2] = src_row[x * 4 + 0]; // R
                dst_row[x * 4 + 3] = src_row[x * 4 + 3]; // A
            }
        } else {
            // BGRA8 or unknown — copy as-is (assume BGRA)
            std::memcpy(dst_row, src_row, src_w * 4);
        }
    }

    D3D12_RANGE empty_range{0, 0};
    readback->Unmap(0, &empty_range);

    // Cleanup D3D12 resources
    fence->Release();
    if (own_queue) queue->Release();
    cmd->Release();
    alloc->Release();
    readback->Release();
    backbuffer->Release();
    swapchain->Release();

    // Store full-resolution pixels — scaling happens in capture() via WIC
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_bmp = std::move(src_bgra);
        m_result_width = src_w;
        m_result_height = src_h;
        m_result_ready = true;
        m_result_error.clear();
        m_capture_requested.store(false);
    }
    m_cv.notify_all();
}
