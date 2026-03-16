#include "screenshot_capture.h"

#include <d3d11.h>
#include <dxgi.h>
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

void ScreenshotCapture::initialize(void* device, void* swap_chain, int renderer_type) {
    std::lock_guard<std::mutex> lock(m_mutex);
    m_device = device;
    m_swap_chain = swap_chain;
    m_renderer_type = renderer_type;
    m_initialized = true;
}

json ScreenshotCapture::capture(int max_width, int max_height, int quality, int timeout_ms) {
    if (!m_initialized) {
        return json{{"error", "Screenshot capture not initialized. Call initialize() first."}};
    }

    if (m_renderer_type != 1) {
        return json{{"error", "Only D3D11 renderer is currently supported (renderer_type=" + std::to_string(m_renderer_type) + ")"}};
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

    // Encode as JPEG via WIC
    if (quality < 1) quality = 1;
    if (quality > 100) quality = 100;
    auto jpeg_data = encode_jpeg(m_result_bmp.data(), m_result_width, m_result_height, quality);
    if (jpeg_data.empty()) {
        return json{{"error", "JPEG encoding failed (WIC error)"}};
    }
    auto b64 = base64_encode(jpeg_data);

    json result;
    result["width"] = m_result_width;
    result["height"] = m_result_height;
    result["format"] = "jpeg";
    result["quality"] = quality;
    result["size"] = (int)jpeg_data.size();
    result["data"] = b64;
    return result;
}

void ScreenshotCapture::on_present() {
    if (!m_capture_requested.load()) {
        return;
    }

    if (m_renderer_type == 1) {
        capture_d3d11();
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

    // Determine target dimensions
    int max_w, max_h;
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        max_w = m_requested_max_width;
        max_h = m_requested_max_height;
    }

    int dst_w = src_w;
    int dst_h = src_h;

    // Downscale if needed
    if (max_w > 0 && dst_w > max_w) {
        double scale = (double)max_w / (double)dst_w;
        dst_w = max_w;
        dst_h = (int)(src_h * scale);
        if (dst_h < 1) dst_h = 1;
    }
    if (max_h > 0 && dst_h > max_h) {
        double scale = (double)max_h / (double)dst_h;
        dst_h = max_h;
        dst_w = (int)(dst_w * scale);
        if (dst_w < 1) dst_w = 1;
    }

    // Ensure even dimensions for cleaner BMP row alignment
    if (dst_w < 1) dst_w = 1;
    if (dst_h < 1) dst_h = 1;

    std::vector<uint8_t> final_bgra;

    if (dst_w == src_w && dst_h == src_h) {
        // No scaling needed
        final_bgra = std::move(src_bgra);
    } else {
        // Nearest-neighbor downscale for speed
        final_bgra.resize(dst_w * dst_h * 4);
        for (int y = 0; y < dst_h; ++y) {
            int sy = y * src_h / dst_h;
            if (sy >= src_h) sy = src_h - 1;
            for (int x = 0; x < dst_w; ++x) {
                int sx = x * src_w / dst_w;
                if (sx >= src_w) sx = src_w - 1;
                const uint8_t* sp = src_bgra.data() + (sy * src_w + sx) * 4;
                uint8_t* dp = final_bgra.data() + (y * dst_w + x) * 4;
                dp[0] = sp[0];
                dp[1] = sp[1];
                dp[2] = sp[2];
                dp[3] = sp[3];
            }
        }
    }

    // Store result and notify
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        m_result_bmp = std::move(final_bgra);
        m_result_width = dst_w;
        m_result_height = dst_h;
        m_result_ready = true;
        m_result_error.clear();
        m_capture_requested.store(false);
    }
    m_cv.notify_all();
}
