#include "discovery_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/property_reader.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>
#include <windows.h>

#include <algorithm>
#include <sstream>
#include <string>
#include <unordered_set>
#include <vector>

using json = nlohmann::json;
using namespace uevr;

namespace DiscoveryRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

// Case-insensitive substring search
static bool contains_ci(const std::string& haystack, const std::string& needle) {
    if (needle.empty()) return true;
    if (haystack.size() < needle.size()) return false;
    auto it = std::search(
        haystack.begin(), haystack.end(),
        needle.begin(), needle.end(),
        [](char a, char b) { return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b)); }
    );
    return it != haystack.end();
}

void register_routes(httplib::Server& server) {

    // =========================================================================
    // GET /api/discovery/subclasses — Find all subclasses of a given class
    // =========================================================================
    server.Get("/api/discovery/subclasses", [](const httplib::Request& req, httplib::Response& res) {
        auto class_name = req.get_param_value("className");
        if (class_name.empty()) {
            send_json(res, json{{"error", "Missing 'className' parameter"}}, 400);
            return;
        }

        int limit = 100;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        bool include_instances = false;
        if (req.has_param("includeInstances")) {
            auto val = req.get_param_value("includeInstances");
            include_instances = (val == "true" || val == "1");
        }

        PipeServer::get().log("Discovery: subclasses of '" + class_name + "'");

        auto result = GameThreadQueue::get().submit_and_wait([class_name, limit, include_instances]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "UEVR API not available"}};

            // Find the target base class
            auto wname = JsonHelpers::utf8_to_wide(class_name);
            API::UClass* base_cls = api->find_uobject<API::UClass>(wname);
            if (!base_cls) {
                auto prefixed = L"Class " + wname;
                base_cls = api->find_uobject<API::UClass>(prefixed);
            }
            if (!base_cls) {
                return json{{"error", "Base class not found: " + class_name}};
            }

            // Iterate all UClass objects in GUObjectArray
            auto array = API::FUObjectArray::get();
            if (!array) return json{{"error", "GUObjectArray not available"}};

            int count = array->get_object_count();
            json results = json::array();

            for (int i = 0; i < count && (int)results.size() < limit; ++i) {
                auto obj = array->get_object(i);
                if (!obj) continue;

                // Check if this is a UClass by inspecting its metaclass name
                auto cls = obj->get_class();
                if (!cls) continue;
                auto meta_fname = cls->get_fname();
                if (!meta_fname) continue;
                auto meta_name = meta_fname->to_string();
                if (meta_name != L"Class") continue;

                // Cast to UClass and walk super chain to check inheritance
                auto as_class = reinterpret_cast<API::UClass*>(obj);
                bool is_subclass = false;
                for (auto s = as_class->get_super(); s != nullptr; s = s->get_super()) {
                    if (reinterpret_cast<void*>(s) == reinterpret_cast<void*>(base_cls)) {
                        is_subclass = true;
                        break;
                    }
                }

                if (is_subclass) {
                    json entry;
                    entry["address"] = JsonHelpers::address_to_string(obj);
                    entry["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());
                    auto cls_fname = as_class->get_fname();
                    entry["className"] = cls_fname ? JsonHelpers::wide_to_utf8(cls_fname->to_string()) : "<unknown>";

                    // Optionally show if any live instances exist
                    if (include_instances) {
                        auto instances = as_class->get_objects_matching<API::UObject>(false);
                        entry["instanceCount"] = (int)instances.size();
                    }

                    results.push_back(entry);
                }
            }

            json response;
            response["baseClass"] = class_name;
            response["results"] = results;
            response["count"] = (int)results.size();
            return response;
        }, 10000);

        send_json(res, result);
    });

    // =========================================================================
    // GET /api/discovery/names — Search reflection names via GUObjectArray
    // =========================================================================
    server.Get("/api/discovery/names", [](const httplib::Request& req, httplib::Response& res) {
        auto query = req.get_param_value("query");
        if (query.empty()) {
            send_json(res, json{{"error", "Missing 'query' parameter"}}, 400);
            return;
        }

        int limit = 100;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        std::string scope = "all";
        if (req.has_param("scope")) {
            scope = req.get_param_value("scope");
        }

        PipeServer::get().log("Discovery: names query='" + query + "' scope=" + scope);

        auto result = GameThreadQueue::get().submit_and_wait([query, limit, scope]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "UEVR API not available"}};

            std::unordered_set<std::string> seen_names;
            json results = json::array();

            auto array = API::FUObjectArray::get();
            if (!array) return json{{"error", "GUObjectArray not available"}};

            int count = array->get_object_count();

            for (int i = 0; i < count; ++i) {
                if ((int)results.size() >= limit) break;

                auto obj = array->get_object(i);
                if (!obj) continue;

                auto obj_cls = obj->get_class();
                if (!obj_cls) continue;
                auto meta_fname = obj_cls->get_fname();
                if (!meta_fname) continue;
                auto meta_name = meta_fname->to_string();

                // For class-scope matches, check if this object is a UClass and its name matches
                if (scope == "all" || scope == "classes") {
                    if (meta_name == L"Class") {
                        auto fname = obj->get_fname();
                        if (fname) {
                            auto name = JsonHelpers::wide_to_utf8(fname->to_string());
                            if (contains_ci(name, query) && !seen_names.count(name)) {
                                seen_names.insert(name);
                                results.push_back({
                                    {"name", name},
                                    {"source", "class"},
                                    {"address", JsonHelpers::address_to_string(obj)}
                                });
                                if ((int)results.size() >= limit) break;
                            }
                        }
                    }
                }

                // For UStruct-like objects (Class or ScriptStruct), scan properties and functions
                if (meta_name == L"Class" || meta_name == L"ScriptStruct") {
                    auto as_struct = reinterpret_cast<API::UStruct*>(obj);
                    auto owner_fname = obj->get_fname();
                    std::string owner_name = owner_fname ? JsonHelpers::wide_to_utf8(owner_fname->to_string()) : "<unknown>";

                    // Scan properties
                    if (scope == "all" || scope == "properties") {
                        for (auto prop = as_struct->get_child_properties(); prop; prop = prop->get_next()) {
                            if ((int)results.size() >= limit) break;
                            auto pname = prop->get_fname();
                            if (!pname) continue;
                            auto name_str = JsonHelpers::wide_to_utf8(pname->to_string());
                            if (contains_ci(name_str, query) && !seen_names.count(name_str)) {
                                seen_names.insert(name_str);
                                auto pclass = prop->get_class();
                                std::string type_str = pclass ? JsonHelpers::wide_to_utf8(pclass->get_fname()->to_string()) : "Unknown";
                                results.push_back({
                                    {"name", name_str},
                                    {"source", "property"},
                                    {"owner", owner_name},
                                    {"type", type_str}
                                });
                            }
                        }
                    }

                    // Scan functions (children are UField-based, functions have metaclass "Function")
                    if (scope == "all" || scope == "functions") {
                        for (auto child = as_struct->get_children(); child; child = child->get_next()) {
                            if ((int)results.size() >= limit) break;
                            auto child_cls = child->get_class();
                            if (!child_cls) continue;
                            auto child_cls_fname = child_cls->get_fname();
                            if (!child_cls_fname) continue;
                            if (child_cls_fname->to_string() != L"Function") continue;

                            auto fname_obj = child->get_fname();
                            if (!fname_obj) continue;
                            auto name_str = JsonHelpers::wide_to_utf8(fname_obj->to_string());
                            if (contains_ci(name_str, query) && !seen_names.count(name_str)) {
                                seen_names.insert(name_str);
                                results.push_back({
                                    {"name", name_str},
                                    {"source", "function"},
                                    {"owner", owner_name}
                                });
                            }
                        }
                    }
                }
            }

            json response;
            response["query"] = query;
            response["scope"] = scope;
            response["results"] = results;
            response["count"] = (int)results.size();
            return response;
        }, 10000);

        send_json(res, result);
    });

    // =========================================================================
    // GET /api/discovery/delegates — Inspect delegates on an object
    // =========================================================================
    server.Get("/api/discovery/delegates", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        PipeServer::get().log("Discovery: delegates on " + addr_str);

        auto result = GameThreadQueue::get().submit_and_wait([address]() -> json {
            auto obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object not found or dead"}};
            }

            auto cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            auto cls_fname = cls->get_fname();
            std::string class_name = cls_fname ? JsonHelpers::wide_to_utf8(cls_fname->to_string()) : "<unknown>";

            json delegates = json::array();

            // Walk all properties across the inheritance chain, find delegate types
            for (auto current = reinterpret_cast<API::UStruct*>(cls); current != nullptr; current = current->get_super()) {
                for (auto field = current->get_child_properties(); field; field = field->get_next()) {
                    auto fc = field->get_class();
                    if (!fc) continue;
                    auto fc_fname = fc->get_fname();
                    if (!fc_fname) continue;
                    auto type_name = JsonHelpers::wide_to_utf8(fc_fname->to_string());

                    if (type_name.find("DelegateProperty") != std::string::npos ||
                        type_name.find("MulticastDelegateProperty") != std::string::npos ||
                        type_name.find("MulticastInlineDelegateProperty") != std::string::npos ||
                        type_name.find("MulticastSparseDelegateProperty") != std::string::npos) {

                        auto fprop = reinterpret_cast<API::FProperty*>(field);
                        auto prop_fname = field->get_fname();
                        std::string prop_name = prop_fname ? JsonHelpers::wide_to_utf8(prop_fname->to_string()) : "<unknown>";

                        auto current_fname = current->get_fname();
                        std::string declared_in = current_fname ? JsonHelpers::wide_to_utf8(current_fname->to_string()) : "<unknown>";

                        json entry;
                        entry["name"] = prop_name;
                        entry["type"] = type_name;
                        entry["offset"] = fprop->get_offset();
                        entry["declaredIn"] = declared_in;

                        delegates.push_back(entry);
                    }
                }
            }

            // Additionally list any functions that look like events (common UE patterns)
            json event_functions = json::array();
            for (auto current = reinterpret_cast<API::UStruct*>(cls); current != nullptr; current = current->get_super()) {
                for (auto child = current->get_children(); child; child = child->get_next()) {
                    auto child_cls = child->get_class();
                    if (!child_cls) continue;
                    auto child_cls_fname = child_cls->get_fname();
                    if (!child_cls_fname) continue;
                    if (child_cls_fname->to_string() != L"Function") continue;

                    auto func_fname = child->get_fname();
                    if (!func_fname) continue;
                    auto func_name = JsonHelpers::wide_to_utf8(func_fname->to_string());

                    // Check for event-like patterns
                    if (func_name.find("On") == 0 || func_name.find("Receive") == 0 ||
                        func_name.find("Event") != std::string::npos ||
                        func_name.find("Handle") == 0 ||
                        func_name.find("Notify") == 0 ||
                        func_name.find("BP_") == 0 ||
                        func_name.find("K2_") == 0) {

                        auto func = reinterpret_cast<API::UFunction*>(child);
                        json fentry;
                        fentry["name"] = func_name;

                        auto current_fname = current->get_fname();
                        fentry["declaredIn"] = current_fname ? JsonHelpers::wide_to_utf8(current_fname->to_string()) : "<unknown>";

                        // Read function params
                        json params = json::array();
                        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
                            auto pc = p->get_class();
                            auto pn = p->get_fname();
                            if (pn && pc) {
                                auto pc_fname = pc->get_fname();
                                params.push_back({
                                    {"name", JsonHelpers::wide_to_utf8(pn->to_string())},
                                    {"type", pc_fname ? JsonHelpers::wide_to_utf8(pc_fname->to_string()) : "Unknown"}
                                });
                            }
                        }
                        fentry["params"] = params;
                        event_functions.push_back(fentry);
                    }
                }
            }

            json response;
            response["address"] = JsonHelpers::address_to_string((void*)address);
            response["className"] = class_name;
            response["delegates"] = delegates;
            response["eventFunctions"] = event_functions;
            return response;
        });

        send_json(res, result);
    });

    // =========================================================================
    // GET /api/discovery/vtable — Compare vtable between object and base class CDO
    // =========================================================================
    server.Get("/api/discovery/vtable", [](const httplib::Request& req, httplib::Response& res) {
        auto addr_str = req.get_param_value("address");
        if (addr_str.empty()) {
            send_json(res, json{{"error", "Missing 'address' parameter"}}, 400);
            return;
        }

        auto address = JsonHelpers::string_to_address(addr_str);
        if (address == 0) {
            send_json(res, json{{"error", "Invalid address"}}, 400);
            return;
        }

        int entries = 64;
        if (req.has_param("entries")) {
            try { entries = std::stoi(req.get_param_value("entries")); } catch (...) {}
        }

        PipeServer::get().log("Discovery: vtable on " + addr_str + " entries=" + std::to_string(entries));

        auto result = GameThreadQueue::get().submit_and_wait([address, entries]() -> json {
            auto obj = reinterpret_cast<API::UObject*>(address);
            if (!API::UObjectHook::exists(obj)) {
                return json{{"error", "Object not found or dead"}};
            }

            auto cls = obj->get_class();
            if (!cls) return json{{"error", "Object has no class"}};

            auto cls_fname = cls->get_fname();
            std::string class_name = cls_fname ? JsonHelpers::wide_to_utf8(cls_fname->to_string()) : "<unknown>";

            auto cdo = cls->get_class_default_object();

            // Get the vtable (first pointer in the object)
            if (IsBadReadPtr(obj, sizeof(uintptr_t*))) {
                return json{{"error", "Cannot read object vtable pointer"}};
            }
            auto obj_vtable = *reinterpret_cast<uintptr_t**>(obj);

            uintptr_t* cdo_vtable = nullptr;
            if (cdo && !IsBadReadPtr(cdo, sizeof(uintptr_t*))) {
                cdo_vtable = *reinterpret_cast<uintptr_t**>(cdo);
            }

            // Also get the base class's CDO vtable for comparison
            auto super = cls->get_super();
            uintptr_t* base_vtable = nullptr;
            std::string base_class_name;
            if (super) {
                auto super_as_class = reinterpret_cast<API::UClass*>(super);
                auto base_cdo = super_as_class->get_class_default_object();
                if (base_cdo && !IsBadReadPtr(base_cdo, sizeof(uintptr_t*))) {
                    base_vtable = *reinterpret_cast<uintptr_t**>(base_cdo);
                    auto super_fname = super->get_fname();
                    base_class_name = super_fname ? JsonHelpers::wide_to_utf8(super_fname->to_string()) : "<unknown>";
                }
            }

            int num_entries = std::min(entries, 256); // cap at 256
            json vtable_entries = json::array();
            int overridden_count = 0;

            // Read and compare vtable entries
            for (int i = 0; i < num_entries; ++i) {
                // Check if the pointer is readable
                if (IsBadReadPtr(&obj_vtable[i], sizeof(uintptr_t))) break;

                uintptr_t obj_func = obj_vtable[i];
                if (obj_func == 0) break;

                json entry;
                entry["index"] = i;
                entry["address"] = JsonHelpers::address_to_string((void*)obj_func);

                if (base_vtable) {
                    uintptr_t base_func = 0;
                    if (!IsBadReadPtr(&base_vtable[i], sizeof(uintptr_t))) {
                        base_func = base_vtable[i];
                    }

                    entry["baseAddress"] = JsonHelpers::address_to_string((void*)base_func);
                    bool overridden = (base_func != 0 && obj_func != base_func);
                    entry["overridden"] = overridden;
                    if (overridden) ++overridden_count;
                }

                // Try to get module info for the address
                HMODULE hmod = nullptr;
                if (GetModuleHandleExA(
                        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                        (LPCSTR)obj_func, &hmod)) {
                    char modpath[MAX_PATH]{};
                    GetModuleFileNameA(hmod, modpath, MAX_PATH);
                    std::string mp(modpath);
                    auto slash = mp.find_last_of("\\/");
                    entry["module"] = (slash != std::string::npos) ? mp.substr(slash + 1) : mp;
                    entry["rva"] = JsonHelpers::address_to_string((void*)(obj_func - (uintptr_t)hmod));
                }

                vtable_entries.push_back(entry);
            }

            json response;
            response["address"] = JsonHelpers::address_to_string((void*)address);
            response["className"] = class_name;
            if (!base_class_name.empty()) {
                response["baseClass"] = base_class_name;
            }
            response["totalEntries"] = (int)vtable_entries.size();
            response["overriddenCount"] = overridden_count;
            response["entries"] = vtable_entries;
            return response;
        });

        send_json(res, result);
    });

    // =========================================================================
    // POST /api/discovery/pattern_scan — Search executable memory for byte patterns
    // =========================================================================
    server.Post("/api/discovery/pattern_scan", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto pattern_str = body.value("pattern", "");
        auto module_name = body.value("module", "");
        int limit = body.value("limit", 10);

        if (pattern_str.empty()) {
            send_json(res, json{{"error", "Missing 'pattern' in request body"}}, 400);
            return;
        }

        PipeServer::get().log("Discovery: pattern_scan '" + pattern_str + "' in " + (module_name.empty() ? "<main>" : module_name));

        // Pattern scanning reads code memory which is read-only, no need for game thread
        struct PatternByte {
            uint8_t value;
            bool wildcard;
        };
        std::vector<PatternByte> pattern_bytes;

        // Parse "48 89 5C 24 ? 57" format
        std::istringstream iss(pattern_str);
        std::string token;
        while (iss >> token) {
            if (token == "?" || token == "??") {
                pattern_bytes.push_back({0, true});
            } else {
                pattern_bytes.push_back({(uint8_t)strtol(token.c_str(), nullptr, 16), false});
            }
        }

        if (pattern_bytes.empty()) {
            send_json(res, json{{"error", "Empty pattern"}}, 400);
            return;
        }

        // Determine scan range
        HMODULE target_module = nullptr;
        if (!module_name.empty()) {
            target_module = GetModuleHandleA(module_name.c_str());
        } else {
            target_module = GetModuleHandleA(nullptr); // Main exe
        }

        if (!target_module) {
            send_json(res, json{{"error", "Module not found: " + (module_name.empty() ? "<main exe>" : module_name)}}, 404);
            return;
        }

        json matches = json::array();
        std::string resolved_module;

        // Get module file name for the response
        {
            char modpath[MAX_PATH]{};
            GetModuleFileNameA(target_module, modpath, MAX_PATH);
            std::string mp(modpath);
            auto slash = mp.find_last_of("\\/");
            resolved_module = (slash != std::string::npos) ? mp.substr(slash + 1) : mp;
        }

        // Scan this module's executable sections
        auto dos = (PIMAGE_DOS_HEADER)target_module;
        if (IsBadReadPtr(dos, sizeof(IMAGE_DOS_HEADER)) || dos->e_magic != IMAGE_DOS_SIGNATURE) {
            send_json(res, json{{"error", "Invalid DOS header for module"}}, 500);
            return;
        }

        auto nt = (PIMAGE_NT_HEADERS)((uintptr_t)target_module + dos->e_lfanew);
        if (IsBadReadPtr(nt, sizeof(IMAGE_NT_HEADERS)) || nt->Signature != IMAGE_NT_SIGNATURE) {
            send_json(res, json{{"error", "Invalid NT header for module"}}, 500);
            return;
        }

        auto section = IMAGE_FIRST_SECTION(nt);

        for (WORD s = 0; s < nt->FileHeader.NumberOfSections; ++s, ++section) {
            if (!(section->Characteristics & IMAGE_SCN_MEM_EXECUTE)) continue;

            uintptr_t base = (uintptr_t)target_module + section->VirtualAddress;
            size_t size = section->Misc.VirtualSize;

            // Verify we can read this region
            if (IsBadReadPtr((void*)base, size)) continue;

            // Scan for pattern
            for (size_t i = 0; i + pattern_bytes.size() <= size && (int)matches.size() < limit; ++i) {
                bool match = true;
                for (size_t j = 0; j < pattern_bytes.size(); ++j) {
                    if (pattern_bytes[j].wildcard) continue;
                    if (*(uint8_t*)(base + i + j) != pattern_bytes[j].value) {
                        match = false;
                        break;
                    }
                }
                if (match) {
                    uintptr_t addr = base + i;
                    json m;
                    m["address"] = JsonHelpers::address_to_string((void*)addr);
                    m["rva"] = JsonHelpers::address_to_string((void*)(addr - (uintptr_t)target_module));

                    // Read a few bytes around the match for context
                    int ctx_len = std::min((int)pattern_bytes.size() + 8, 32);
                    std::string hex_str;
                    for (int k = 0; k < ctx_len && (i + k) < size; ++k) {
                        char byte_hex[4];
                        snprintf(byte_hex, sizeof(byte_hex), "%02X ", *(uint8_t*)(addr + k));
                        hex_str += byte_hex;
                    }
                    // Trim trailing space
                    if (!hex_str.empty() && hex_str.back() == ' ') {
                        hex_str.pop_back();
                    }
                    m["bytes"] = hex_str;
                    matches.push_back(m);
                }
            }

            if ((int)matches.size() >= limit) break;
        }

        json response;
        response["pattern"] = pattern_str;
        response["module"] = resolved_module;
        response["results"] = matches;
        response["count"] = (int)matches.size();
        send_json(res, response);
    });

    // =========================================================================
    // GET /api/discovery/all_children — Enumerate ALL children of a type
    // =========================================================================
    server.Get("/api/discovery/all_children", [](const httplib::Request& req, httplib::Response& res) {
        auto type_name = req.get_param_value("typeName");
        if (type_name.empty()) {
            send_json(res, json{{"error", "Missing 'typeName' parameter"}}, 400);
            return;
        }

        int depth = 1;
        if (req.has_param("depth")) {
            try { depth = std::stoi(req.get_param_value("depth")); } catch (...) {}
        }

        PipeServer::get().log("Discovery: all_children of '" + type_name + "' depth=" + std::to_string(depth));

        auto result = GameThreadQueue::get().submit_and_wait([type_name, depth]() -> json {
            auto& api = API::get();
            if (!api) return json{{"error", "UEVR API not available"}};

            // Find the type
            auto wname = JsonHelpers::utf8_to_wide(type_name);
            auto cls = api->find_uobject<API::UStruct>(wname);
            if (!cls) {
                auto prefixed = L"Class " + wname;
                cls = api->find_uobject<API::UStruct>(prefixed);
            }
            if (!cls) {
                return json{{"error", "Type not found: " + type_name}};
            }

            json all_properties = json::array();
            json all_functions = json::array();
            json super_chain = json::array();

            int max_depth = depth > 0 ? depth : 100; // how many super levels to walk
            int levels = 0;

            for (auto current = cls; current != nullptr && levels < max_depth; current = current->get_super(), ++levels) {
                auto current_fname = current->get_fname();
                std::string level_name = current_fname ? JsonHelpers::wide_to_utf8(current_fname->to_string()) : "<unknown>";
                super_chain.push_back(level_name);

                // All properties at this level
                for (auto field = current->get_child_properties(); field; field = field->get_next()) {
                    auto fc = field->get_class();
                    auto fn = field->get_fname();
                    if (!fn) continue;

                    json entry;
                    entry["name"] = JsonHelpers::wide_to_utf8(fn->to_string());
                    entry["declaredIn"] = level_name;
                    if (fc) {
                        auto fc_fname = fc->get_fname();
                        entry["type"] = fc_fname ? JsonHelpers::wide_to_utf8(fc_fname->to_string()) : "Unknown";
                    }

                    auto fprop = reinterpret_cast<API::FProperty*>(field);
                    entry["offset"] = fprop->get_offset();

                    all_properties.push_back(entry);
                }

                // All functions at this level (including non-standard ones like DelegateFunction)
                for (auto child = current->get_children(); child; child = child->get_next()) {
                    auto child_cls = child->get_class();
                    if (!child_cls) continue;

                    auto child_cls_fname = child_cls->get_fname();
                    if (!child_cls_fname) continue;
                    auto child_type = JsonHelpers::wide_to_utf8(child_cls_fname->to_string());

                    auto child_name_ptr = child->get_fname();
                    if (!child_name_ptr) continue;
                    auto child_name = JsonHelpers::wide_to_utf8(child_name_ptr->to_string());

                    json entry;
                    entry["name"] = child_name;
                    entry["type"] = child_type; // "Function", "DelegateFunction", "SparseDelegateFunction", etc.
                    entry["declaredIn"] = level_name;

                    // If it's a function or delegate function, enumerate its parameters
                    if (child_type == "Function" || child_type == "DelegateFunction") {
                        auto func = reinterpret_cast<API::UFunction*>(child);
                        json params = json::array();
                        std::string return_type;

                        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
                            auto pc = p->get_class();
                            auto pn = p->get_fname();
                            if (!pn || !pc) continue;

                            auto fp = reinterpret_cast<API::FProperty*>(p);
                            bool is_return = fp->is_return_param();
                            bool is_out = fp->is_out_param();

                            if (is_return) {
                                auto pc_fname = pc->get_fname();
                                return_type = pc_fname ? JsonHelpers::wide_to_utf8(pc_fname->to_string()) : "Unknown";
                            } else {
                                auto pc_fname = pc->get_fname();
                                params.push_back({
                                    {"name", JsonHelpers::wide_to_utf8(pn->to_string())},
                                    {"type", pc_fname ? JsonHelpers::wide_to_utf8(pc_fname->to_string()) : "Unknown"},
                                    {"isOut", is_out}
                                });
                            }
                        }

                        entry["params"] = params;
                        if (!return_type.empty()) entry["returnType"] = return_type;
                    }

                    all_functions.push_back(entry);
                }
            }

            json response;
            response["typeName"] = type_name;
            response["superChain"] = super_chain;
            response["properties"] = all_properties;
            response["functions"] = all_functions;
            response["propertyCount"] = (int)all_properties.size();
            response["functionCount"] = (int)all_functions.size();
            return response;
        }, 10000);

        send_json(res, result);
    });
}

} // namespace DiscoveryRoutes
