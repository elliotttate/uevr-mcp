#pragma once

#include <string>
#include <cstdint>
#include <nlohmann/json.hpp>

using json = nlohmann::json;

namespace JsonHelpers {
    // Convert wide string to UTF-8
    std::string wide_to_utf8(const std::wstring& wstr);
    std::string wide_to_utf8(const wchar_t* wstr, size_t len);

    // Convert UTF-8 to wide string
    std::wstring utf8_to_wide(const std::string& str);

    // Format a pointer address as hex string
    std::string address_to_string(void* ptr);
    std::string address_to_string(uintptr_t addr);

    // Parse hex address string to uintptr_t. Returns 0 on failure.
    uintptr_t string_to_address(const std::string& str);

    // FName to UTF-8 string (using UEVR API)
    std::string fname_to_string(void* fname);
}
