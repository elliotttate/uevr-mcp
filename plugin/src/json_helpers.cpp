#include "json_helpers.h"

#include <Windows.h>
#include <sstream>
#include <iomanip>

#include <uevr/API.hpp>

namespace JsonHelpers {

std::string wide_to_utf8(const std::wstring& wstr) {
    if (wstr.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr.data(), (int)wstr.size(), result.data(), size, nullptr, nullptr);
    return result;
}

std::string wide_to_utf8(const wchar_t* wstr, size_t len) {
    if (!wstr || len == 0) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, wstr, (int)len, nullptr, 0, nullptr, nullptr);
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, wstr, (int)len, result.data(), size, nullptr, nullptr);
    return result;
}

std::wstring utf8_to_wide(const std::string& str) {
    if (str.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), nullptr, 0);
    std::wstring result(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, str.data(), (int)str.size(), result.data(), size);
    return result;
}

std::string address_to_string(void* ptr) {
    return address_to_string(reinterpret_cast<uintptr_t>(ptr));
}

std::string address_to_string(uintptr_t addr) {
    std::ostringstream oss;
    oss << "0x" << std::hex << std::uppercase << addr;
    return oss.str();
}

uintptr_t string_to_address(const std::string& str) {
    try {
        size_t pos = 0;
        // Handle "0x" prefix
        if (str.size() > 2 && str[0] == '0' && (str[1] == 'x' || str[1] == 'X')) {
            return std::stoull(str.substr(2), &pos, 16);
        }
        return std::stoull(str, &pos, 16);
    } catch (...) {
        return 0;
    }
}

std::string fname_to_string(void* fname) {
    if (!fname) return "<null>";
    auto& api = uevr::API::get();
    if (!api) return "<no api>";

    auto fn = reinterpret_cast<uevr::API::FName*>(fname);
    auto wstr = fn->to_string();
    return wide_to_utf8(wstr);
}

} // namespace JsonHelpers
