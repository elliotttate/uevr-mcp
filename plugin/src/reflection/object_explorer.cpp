#include "object_explorer.h"
#include "property_reader.h"
#include "function_caller.h"
#include "../json_helpers.h"

#include <algorithm>
#include <cctype>
#include <cstring>

using namespace uevr;

// Case-insensitive substring search
static bool contains_ci(const std::string& haystack, const std::string& needle) {
    auto it = std::search(haystack.begin(), haystack.end(),
        needle.begin(), needle.end(),
        [](char a, char b) { return std::tolower((unsigned char)a) == std::tolower((unsigned char)b); });
    return it != haystack.end();
}

namespace ObjectExplorer {

json search_objects(const std::string& query, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    json results = json::array();
    int count = array->get_object_count();

    for (int i = 0; i < count && (int)results.size() < limit; ++i) {
        auto obj = array->get_object(i);
        if (!obj) continue;

        auto full_name = obj->get_full_name();
        auto name_utf8 = JsonHelpers::wide_to_utf8(full_name);

        if (contains_ci(name_utf8, query)) {
            json entry;
            entry["address"] = JsonHelpers::address_to_string(obj);
            entry["fullName"] = name_utf8;

            auto cls = obj->get_class();
            if (cls && cls->get_fname()) {
                entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            }

            results.push_back(entry);
        }
    }

    return json{{"results", results}, {"count", results.size()}, {"total_scanned", count}};
}

json search_classes(const std::string& query, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto array = API::FUObjectArray::get();
    if (!array) return json{{"error", "GUObjectArray not available"}};

    json results = json::array();
    int count = array->get_object_count();

    for (int i = 0; i < count && (int)results.size() < limit; ++i) {
        auto obj = array->get_object(i);
        if (!obj) continue;

        auto cls = obj->get_class();
        if (!cls) continue;

        // Check if this is a UClass (class of class should be "Class")
        auto meta_cls = cls->get_class();
        if (!meta_cls) continue;
        auto meta_name = meta_cls->get_fname()->to_string();
        if (meta_name != L"Class") continue;

        auto full_name = obj->get_full_name();
        auto name_utf8 = JsonHelpers::wide_to_utf8(full_name);

        if (contains_ci(name_utf8, query)) {
            json entry;
            entry["address"] = JsonHelpers::address_to_string(obj);
            entry["fullName"] = name_utf8;
            entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            results.push_back(entry);
        }
    }

    return json{{"results", results}, {"count", results.size()}};
}

json enumerate_fields(API::UStruct* ustruct) {
    json fields = json::array();
    if (!ustruct) return fields;

    for (auto current = ustruct; current != nullptr; current = current->get_super()) {
        for (auto field = current->get_child_properties(); field != nullptr; field = field->get_next()) {
            auto fc = field->get_class();
            if (!fc) continue;

            auto class_name_w = fc->get_fname()->to_string();
            auto class_name = JsonHelpers::wide_to_utf8(class_name_w);

            if (class_name.find("Property") == std::string::npos) continue;

            auto fprop = reinterpret_cast<API::FProperty*>(field);
            auto name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());

            json f;
            f["name"] = name;
            f["type"] = class_name;
            f["offset"] = fprop->get_offset();

            // Extra info for specific types
            if (class_name == "StructProperty") {
                auto sp = reinterpret_cast<API::FStructProperty*>(fprop);
                auto ss = sp->get_struct();
                if (ss && ss->get_fname()) {
                    f["structType"] = JsonHelpers::wide_to_utf8(ss->get_fname()->to_string());
                }
            } else if (class_name == "ObjectProperty") {
                // ObjectProperty doesn't expose the class in the C API easily
            } else if (class_name == "ArrayProperty") {
                auto ap = reinterpret_cast<API::FArrayProperty*>(fprop);
                auto inner = ap->get_inner();
                if (inner) {
                    auto inner_c = inner->get_class();
                    if (inner_c && inner_c->get_fname()) {
                        f["innerType"] = JsonHelpers::wide_to_utf8(inner_c->get_fname()->to_string());
                    }
                }
            } else if (class_name == "EnumProperty") {
                auto ep = reinterpret_cast<API::FEnumProperty*>(fprop);
                auto uenum = ep->get_enum();
                if (uenum && uenum->get_fname()) {
                    f["enumType"] = JsonHelpers::wide_to_utf8(uenum->get_fname()->to_string());
                }
            }

            // Owner class
            auto owner_name = current->get_fname();
            if (owner_name) f["owner"] = JsonHelpers::wide_to_utf8(owner_name->to_string());

            fields.push_back(f);
        }
    }

    return fields;
}

json enumerate_methods(API::UStruct* ustruct) {
    json methods = json::array();
    if (!ustruct) return methods;

    for (auto current = ustruct; current != nullptr; current = current->get_super()) {
        // Walk UField children chain — functions are UField children linked via get_next()
        for (auto child = current->get_children(); child != nullptr; child = child->get_next()) {
            auto child_cls = child->get_class();
            if (!child_cls) continue;

            auto child_cls_name = child_cls->get_fname()->to_string();
            if (child_cls_name != L"Function") continue;

            auto func = reinterpret_cast<API::UFunction*>(child);
            auto name = JsonHelpers::wide_to_utf8(func->get_fname()->to_string());

            json m;
            m["name"] = name;

            // Enumerate parameters
            json params = json::array();
            std::string return_type;

            for (auto param = func->get_child_properties(); param != nullptr; param = param->get_next()) {
                auto pc = param->get_class();
                if (!pc) continue;
                auto pc_name = JsonHelpers::wide_to_utf8(pc->get_fname()->to_string());
                if (pc_name.find("Property") == std::string::npos) continue;

                auto fparam = reinterpret_cast<API::FProperty*>(param);
                auto pname = JsonHelpers::wide_to_utf8(fparam->get_fname()->to_string());

                if (fparam->is_return_param()) {
                    return_type = pc_name;
                    continue;
                }

                json p;
                p["name"] = pname;
                p["type"] = pc_name;
                if (fparam->is_out_param()) p["out"] = true;
                params.push_back(p);
            }

            m["params"] = params;
            if (!return_type.empty()) m["returnType"] = return_type;

            auto owner_name = current->get_fname();
            if (owner_name) m["owner"] = JsonHelpers::wide_to_utf8(owner_name->to_string());

            methods.push_back(m);
        }
    }

    return methods;
}

json get_type_info(const std::string& type_name) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(type_name);

    // Try finding as-is first
    auto ustruct = api->find_uobject<API::UStruct>(wname);

    // Try common prefixes
    if (!ustruct) ustruct = api->find_uobject<API::UStruct>(L"Class " + wname);
    if (!ustruct) ustruct = api->find_uobject<API::UStruct>(L"ScriptStruct " + wname);

    if (!ustruct) {
        return json{{"error", "Type not found: " + type_name}};
    }

    json result;
    result["name"] = JsonHelpers::wide_to_utf8(ustruct->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(ustruct->get_full_name());
    result["address"] = JsonHelpers::address_to_string(ustruct);
    result["propertiesSize"] = ustruct->get_properties_size();

    auto super = ustruct->get_super();
    if (super && super->get_fname()) {
        result["super"] = JsonHelpers::wide_to_utf8(super->get_fname()->to_string());
    }

    result["fields"] = enumerate_fields(ustruct);
    result["methods"] = enumerate_methods(ustruct);

    return result;
}

json inspect_object(uintptr_t address, int depth) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    // Validate object is alive
    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid at " + JsonHelpers::address_to_string(address)}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    json result;
    result["address"] = JsonHelpers::address_to_string(address);
    result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

    auto super = cls->get_super();
    if (super && super->get_fname()) {
        result["super"] = JsonHelpers::wide_to_utf8(super->get_fname()->to_string());
    }

    result["fields"] = PropertyReader::read_all_properties(obj, cls, depth);
    result["methods"] = enumerate_methods(cls);

    return result;
}

json summarize_object(uintptr_t address) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    json result;
    result["address"] = JsonHelpers::address_to_string(address);
    result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
    result["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

    // One-line summaries of each field
    json fields = json::object();
    for (API::UStruct* current = cls; current != nullptr; current = current->get_super()) {
        for (auto field = current->get_child_properties(); field != nullptr; field = field->get_next()) {
            auto fc = field->get_class();
            if (!fc) continue;
            auto class_name = fc->get_fname()->to_string();
            if (class_name.find(L"Property") == std::wstring::npos) continue;

            auto fprop = reinterpret_cast<API::FProperty*>(field);
            auto name = JsonHelpers::wide_to_utf8(fprop->get_fname()->to_string());
            auto type = JsonHelpers::wide_to_utf8(class_name);

            try {
                // Read with depth=0 for compact output
                auto val = PropertyReader::read_property(obj, fprop, 0);
                // Summarize: for objects just show address, for structs just show "<struct>"
                if (val.is_object() && val.contains("address")) {
                    fields[name] = type + " -> " + val.value("className", "?") + " @ " + val.value("address", "?");
                } else if (val.is_string()) {
                    auto s = val.get<std::string>();
                    if (s.size() > 80) s = s.substr(0, 80) + "...";
                    fields[name] = type + " = \"" + s + "\"";
                } else if (val.is_number()) {
                    fields[name] = type + " = " + val.dump();
                } else if (val.is_boolean()) {
                    fields[name] = type + " = " + (val.get<bool>() ? "true" : "false");
                } else if (val.is_null()) {
                    fields[name] = type + " = null";
                } else {
                    fields[name] = type;
                }
            } catch (...) {
                fields[name] = type + " = <error>";
            }
        }
    }
    result["fields"] = fields;

    return result;
}

json read_field(uintptr_t address, const std::string& field_name) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(field_name);
    auto prop = cls->find_property(wname.c_str());
    if (!prop) {
        return json{{"error", "Field '" + field_name + "' not found"}};
    }

    json result;
    result["field"] = field_name;
    result["type"] = PropertyReader::get_property_type_name(prop);
    result["value"] = PropertyReader::read_property(obj, prop, 3);

    return result;
}

json find_objects_by_class(const std::string& class_name, int limit) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(class_name);

    // Find the class
    auto cls = api->find_uobject<API::UClass>(L"Class " + wname);
    if (!cls) cls = api->find_uobject<API::UClass>(wname);
    if (!cls) {
        return json{{"error", "Class not found: " + class_name}};
    }

    // Use get_objects_matching to find instances
    auto instances = cls->get_objects_matching<API::UObject>(false);

    json results = json::array();
    int count = 0;
    for (auto inst : instances) {
        if (count >= limit) break;
        if (!inst) continue;

        json entry;
        entry["address"] = JsonHelpers::address_to_string(inst);
        entry["fullName"] = JsonHelpers::wide_to_utf8(inst->get_full_name());
        auto ic = inst->get_class();
        if (ic && ic->get_fname()) {
            entry["className"] = JsonHelpers::wide_to_utf8(ic->get_fname()->to_string());
        }
        results.push_back(entry);
        count++;
    }

    return json{{"className", class_name}, {"results", results}, {"count", results.size()}};
}

json chain_query(uintptr_t start_address, const json& steps) {
    if (!steps.is_array() || steps.empty()) {
        return json{{"error", "Steps must be a non-empty array"}};
    }

    // Working set: list of object addresses we're currently operating on
    std::vector<uintptr_t> current = {start_address};
    static constexpr int MAX_SET_SIZE = 500;
    static constexpr int MAX_STEPS = 20;

    int step_idx = 0;
    for (const auto& step : steps) {
        if (step_idx++ >= MAX_STEPS) {
            return json{{"error", "Too many steps (max 20)"}};
        }

        auto type = step.value("type", "");

        if (type == "field") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "field step requires 'name'"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = read_field(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["value"];
                if (val.is_object() && val.contains("address")) {
                    auto child = JsonHelpers::string_to_address(val["address"].get<std::string>());
                    if (child != 0) next.push_back(child);
                }
            }
            current = next;
        }
        else if (type == "method") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "method step requires 'name'"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = FunctionCaller::call_getter(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["result"];
                if (val.is_object() && val.contains("address")) {
                    auto child = JsonHelpers::string_to_address(val["address"].get<std::string>());
                    if (child != 0) next.push_back(child);
                }
            }
            current = next;
        }
        else if (type == "array") {
            auto name = step.value("name", "");
            if (name.empty()) return json{{"error", "array step requires 'name' (field name of array)"}};

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                auto result = read_field(addr, name);
                if (result.contains("error")) continue;
                auto& val = result["value"];
                if (val.is_array()) {
                    for (auto& elem : val) {
                        if (elem.is_object() && elem.contains("address")) {
                            auto child = JsonHelpers::string_to_address(elem["address"].get<std::string>());
                            if (child != 0) {
                                next.push_back(child);
                                if ((int)next.size() >= MAX_SET_SIZE) break;
                            }
                        }
                    }
                }
                if ((int)next.size() >= MAX_SET_SIZE) break;
            }
            current = next;
        }
        else if (type == "filter") {
            auto method = step.value("method", "");
            auto field = step.value("field", "");
            auto expected = step.value("value", json(true));

            std::vector<uintptr_t> next;
            for (auto addr : current) {
                bool pass = false;
                if (!method.empty()) {
                    auto result = FunctionCaller::call_getter(addr, method);
                    if (result.contains("result")) {
                        pass = (result["result"] == expected);
                    }
                } else if (!field.empty()) {
                    auto result = read_field(addr, field);
                    if (result.contains("value")) {
                        pass = (result["value"] == expected);
                    }
                }
                if (pass) next.push_back(addr);
            }
            current = next;
        }
        else if (type == "collect") {
            auto fields = step.value("fields", json::array());
            auto methods = step.value("methods", json::array());

            json results = json::array();
            for (auto addr : current) {
                json entry;
                entry["address"] = JsonHelpers::address_to_string(addr);

                // Get class name
                auto obj = reinterpret_cast<API::UObject*>(addr);
                if (API::UObjectHook::exists(obj)) {
                    auto cls = obj->get_class();
                    if (cls && cls->get_fname()) {
                        entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
                    }
                }

                for (const auto& f : fields) {
                    auto fname = f.get<std::string>();
                    auto result = read_field(addr, fname);
                    entry[fname] = result.contains("value") ? result["value"] : result;
                }
                for (const auto& m : methods) {
                    auto mname = m.get<std::string>();
                    auto result = FunctionCaller::call_getter(addr, mname);
                    entry[mname] = result.contains("result") ? result["result"] : result;
                }

                results.push_back(entry);
            }
            return json{{"results", results}, {"count", results.size()}};
        }
        else {
            return json{{"error", "Unknown step type: " + type}};
        }

        if (current.empty()) {
            return json{{"results", json::array()}, {"count", 0}, {"note", "Chain ended at step " + std::to_string(step_idx) + " (no results)"}};
        }
    }

    // No collect step — return final addresses
    json results = json::array();
    for (auto addr : current) {
        json entry;
        entry["address"] = JsonHelpers::address_to_string(addr);
        auto obj = reinterpret_cast<API::UObject*>(addr);
        if (API::UObjectHook::exists(obj)) {
            auto cls = obj->get_class();
            if (cls && cls->get_fname()) {
                entry["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
            }
            entry["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());
        }
        results.push_back(entry);
    }
    return json{{"addresses", results}, {"count", results.size()}};
}

json get_singletons() {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    json singletons = json::array();

    // Game Engine
    auto engine = api->get_engine();
    if (engine) {
        json e;
        e["name"] = "GameEngine";
        e["address"] = JsonHelpers::address_to_string(engine);
        auto cls = engine->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);

        // Navigate engine to find GameInstance and World
        auto engine_cls = engine->get_class();
        if (engine_cls) {
            // Try GameInstance field
            auto gi_prop = engine_cls->find_property(L"GameInstance");
            if (gi_prop) {
                auto gi_offset = reinterpret_cast<API::FProperty*>(gi_prop)->get_offset();
                auto gi = *reinterpret_cast<API::UObject**>(reinterpret_cast<uintptr_t>(engine) + gi_offset);
                if (gi) {
                    json g;
                    g["name"] = "GameInstance";
                    g["address"] = JsonHelpers::address_to_string(gi);
                    auto gc = gi->get_class();
                    if (gc && gc->get_fname()) g["className"] = JsonHelpers::wide_to_utf8(gc->get_fname()->to_string());
                    singletons.push_back(g);
                }
            }
        }
    }

    // Player Controller
    auto controller = api->get_player_controller(0);
    if (controller) {
        json e;
        e["name"] = "PlayerController";
        e["address"] = JsonHelpers::address_to_string(controller);
        auto cls = controller->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);
    }

    // Local Pawn
    auto pawn = api->get_local_pawn(0);
    if (pawn) {
        json e;
        e["name"] = "LocalPawn";
        e["address"] = JsonHelpers::address_to_string(pawn);
        auto cls = pawn->get_class();
        if (cls && cls->get_fname()) e["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        singletons.push_back(e);
    }

    // Search for common singleton types via GUObjectArray
    auto array = API::FUObjectArray::get();
    if (array) {
        int count = array->get_object_count();

        // Types we look for as singletons
        static const std::vector<std::wstring> singleton_types = {
            L"World", L"GameMode", L"GameModeBase",
            L"GameState", L"GameStateBase",
            L"GameSession",
            L"HUD", L"CheatManager"
        };

        for (int i = 0; i < count; ++i) {
            auto obj = array->get_object(i);
            if (!obj) continue;

            auto cls = obj->get_class();
            if (!cls) continue;

            auto cls_name = cls->get_fname()->to_string();
            auto meta_cls = cls->get_class();
            if (!meta_cls) continue;

            // Check if this object's class name matches any of our singleton types
            for (const auto& target : singleton_types) {
                if (cls_name.find(target) != std::wstring::npos) {
                    // Check it's not a CDO (default object)
                    auto full = obj->get_full_name();
                    if (full.find(L"Default__") != std::wstring::npos) continue;

                    json e;
                    e["name"] = JsonHelpers::wide_to_utf8(cls_name);
                    e["address"] = JsonHelpers::address_to_string(obj);
                    e["fullName"] = JsonHelpers::wide_to_utf8(full);
                    e["className"] = JsonHelpers::wide_to_utf8(cls_name);
                    singletons.push_back(e);
                    break;
                }
            }
        }
    }

    return json{{"singletons", singletons}, {"count", singletons.size()}};
}

json get_singleton(const std::string& type_name) {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto wname = JsonHelpers::utf8_to_wide(type_name);

    // Try to find the class
    auto cls = api->find_uobject<API::UClass>(L"Class " + wname);
    if (!cls) cls = api->find_uobject<API::UClass>(wname);
    if (!cls) {
        return json{{"error", "Class not found: " + type_name}};
    }

    // Find the first live instance
    auto instances = cls->get_objects_matching<API::UObject>(false);
    for (auto inst : instances) {
        if (!inst) continue;
        // Skip default objects
        auto full = inst->get_full_name();
        if (full.find(L"Default__") != std::wstring::npos) continue;

        json result;
        result["address"] = JsonHelpers::address_to_string(inst);
        result["className"] = JsonHelpers::wide_to_utf8(cls->get_fname()->to_string());
        result["fullName"] = JsonHelpers::wide_to_utf8(full);
        return result;
    }

    return json{{"error", "No live instance of " + type_name}};
}

json read_array(uintptr_t address, const std::string& field_name, int offset, int limit) {
    auto obj = reinterpret_cast<API::UObject*>(address);

    if (!API::UObjectHook::exists(obj)) {
        return json{{"error", "Object no longer valid"}};
    }

    auto cls = obj->get_class();
    if (!cls) return json{{"error", "Object has no class"}};

    auto wname = JsonHelpers::utf8_to_wide(field_name);
    auto prop = cls->find_property(wname.c_str());
    if (!prop) return json{{"error", "Field not found: " + field_name}};

    // Get property type
    auto propc = reinterpret_cast<API::FProperty*>(prop)->get_class();
    if (!propc) return json{{"error", "Property has no class"}};
    auto type_str = propc->get_fname()->to_string();

    if (type_str != L"ArrayProperty") {
        return json{{"error", "Field '" + field_name + "' is not an array"}};
    }

    auto ap = reinterpret_cast<API::FArrayProperty*>(prop);
    auto inner = ap->get_inner();
    if (!inner) return json{{"error", "Array has no inner type"}};

    auto inner_c = inner->get_class();
    if (!inner_c) return json{{"error", "Inner type has no class"}};
    auto inner_type = inner_c->get_fname()->to_string();

    auto fprop = reinterpret_cast<API::FProperty*>(prop);
    auto prop_addr = reinterpret_cast<uintptr_t>(obj) + fprop->get_offset();

    struct TArrayRaw { void* data; int32_t count; int32_t capacity; };
    auto& arr = *reinterpret_cast<TArrayRaw*>(prop_addr);

    int total = arr.count;
    if (!arr.data || total <= 0) {
        return json{{"field", field_name}, {"totalCount", 0}, {"offset", offset}, {"limit", limit}, {"elements", json::array()}};
    }

    if (offset < 0) offset = 0;
    if (offset >= total) {
        return json{{"field", field_name}, {"totalCount", total}, {"offset", offset}, {"limit", limit}, {"elements", json::array()}};
    }

    int end = std::min(offset + limit, total);

    json elements = json::array();
    auto inner_type_utf8 = JsonHelpers::wide_to_utf8(inner_type);

    if (inner_type == L"ObjectProperty" || inner_type == L"InterfaceProperty") {
        auto objs = reinterpret_cast<API::UObject**>(arr.data);
        for (int i = offset; i < end; ++i) {
            if (!objs[i]) { elements.push_back(nullptr); continue; }
            json elem;
            elem["index"] = i;
            elem["address"] = JsonHelpers::address_to_string(objs[i]);
            auto ec = objs[i]->get_class();
            if (ec && ec->get_fname()) elem["className"] = JsonHelpers::wide_to_utf8(ec->get_fname()->to_string());
            elements.push_back(elem);
        }
    } else if (inner_type == L"StructProperty") {
        auto inner_sp = reinterpret_cast<API::FStructProperty*>(inner);
        auto inner_struct = inner_sp->get_struct();
        if (inner_struct) {
            auto elem_size = inner_struct->get_struct_size();
            for (int i = offset; i < end; ++i) {
                auto elem_addr = reinterpret_cast<uintptr_t>(arr.data) + i * elem_size;
                json elem;
                elem["index"] = i;
                elem["value"] = PropertyReader::read_property(reinterpret_cast<void*>(prop_addr), inner, 2);
                // Read individual struct fields
                for (auto sp = inner_struct->get_child_properties(); sp; sp = sp->get_next()) {
                    auto spc = sp->get_class();
                    if (!spc) continue;
                    auto scn = spc->get_fname()->to_string();
                    if (scn.find(L"Property") == std::wstring::npos) continue;
                    auto sfp = reinterpret_cast<API::FProperty*>(sp);
                    auto sname = JsonHelpers::wide_to_utf8(sfp->get_fname()->to_string());
                    elem[sname] = PropertyReader::read_property(reinterpret_cast<void*>(elem_addr), sfp, 2);
                }
                elements.push_back(elem);
            }
        }
    } else if (inner_type == L"FloatProperty") {
        auto vals = reinterpret_cast<float*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"DoubleProperty") {
        auto vals = reinterpret_cast<double*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"IntProperty") {
        auto vals = reinterpret_cast<int32_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"ByteProperty") {
        auto vals = reinterpret_cast<uint8_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i]}});
    } else if (inner_type == L"BoolProperty") {
        auto vals = reinterpret_cast<uint8_t*>(arr.data);
        for (int i = offset; i < end; ++i) elements.push_back(json{{"index", i}, {"value", vals[i] != 0}});
    } else if (inner_type == L"NameProperty") {
        auto fnames = reinterpret_cast<API::FName*>(arr.data);
        for (int i = offset; i < end; ++i) {
            elements.push_back(json{{"index", i}, {"value", JsonHelpers::wide_to_utf8(fnames[i].to_string())}});
        }
    } else {
        return json{{"field", field_name}, {"totalCount", total}, {"innerType", inner_type_utf8}, {"note", "Unsupported array element type for pagination"}};
    }

    return json{
        {"field", field_name}, {"innerType", inner_type_utf8},
        {"totalCount", total}, {"offset", offset}, {"limit", limit},
        {"returnedCount", elements.size()}, {"elements", elements}
    };
}

json read_memory(uintptr_t address, int size) {
    if (size <= 0 || size > 8192) {
        return json{{"error", "Size must be 1-8192 bytes"}};
    }

    std::vector<uint8_t> buffer(size, 0);
    try {
        memcpy(buffer.data(), reinterpret_cast<void*>(address), size);
    } catch (...) {
        return json{{"error", "Access violation reading memory at " + JsonHelpers::address_to_string(address)}};
    }

    // Format as hex dump with ASCII sidebar
    std::string hex_dump;
    for (int row = 0; row < size; row += 16) {
        // Address column
        char addr_buf[32];
        snprintf(addr_buf, sizeof(addr_buf), "%016llX  ", (unsigned long long)(address + row));
        hex_dump += addr_buf;

        // Hex bytes
        for (int col = 0; col < 16; col++) {
            int idx = row + col;
            if (idx < size) {
                char hex[4];
                snprintf(hex, sizeof(hex), "%02X ", buffer[idx]);
                hex_dump += hex;
            } else {
                hex_dump += "   ";
            }
            if (col == 7) hex_dump += " ";
        }

        // ASCII sidebar
        hex_dump += " |";
        for (int col = 0; col < 16 && (row + col) < size; col++) {
            uint8_t b = buffer[row + col];
            hex_dump += (b >= 0x20 && b < 0x7F) ? (char)b : '.';
        }
        hex_dump += "|\n";
    }

    return json{
        {"address", JsonHelpers::address_to_string(address)},
        {"size", size},
        {"dump", hex_dump}
    };
}

json read_typed(uintptr_t address, const std::string& type, int count, int stride) {
    if (count <= 0) count = 1;
    if (count > 50) count = 50;

    // Auto-calculate stride from type if not specified
    if (stride <= 0) {
        if (type == "u8" || type == "i8") stride = 1;
        else if (type == "u16" || type == "i16") stride = 2;
        else if (type == "u32" || type == "i32" || type == "f32") stride = 4;
        else if (type == "u64" || type == "i64" || type == "f64" || type == "ptr") stride = 8;
        else return json{{"error", "Unknown type: " + type + ". Valid: u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, ptr"}};
    }

    json values = json::array();
    for (int i = 0; i < count; i++) {
        uintptr_t addr = address + (uintptr_t)i * stride;
        try {
            if (type == "u8") values.push_back(*reinterpret_cast<uint8_t*>(addr));
            else if (type == "i8") values.push_back(*reinterpret_cast<int8_t*>(addr));
            else if (type == "u16") values.push_back(*reinterpret_cast<uint16_t*>(addr));
            else if (type == "i16") values.push_back(*reinterpret_cast<int16_t*>(addr));
            else if (type == "u32") values.push_back(*reinterpret_cast<uint32_t*>(addr));
            else if (type == "i32") values.push_back(*reinterpret_cast<int32_t*>(addr));
            else if (type == "u64") values.push_back(*reinterpret_cast<uint64_t*>(addr));
            else if (type == "i64") values.push_back(*reinterpret_cast<int64_t*>(addr));
            else if (type == "f32") values.push_back(*reinterpret_cast<float*>(addr));
            else if (type == "f64") values.push_back(*reinterpret_cast<double*>(addr));
            else if (type == "ptr") values.push_back(JsonHelpers::address_to_string(*reinterpret_cast<uintptr_t*>(addr)));
        } catch (...) {
            values.push_back(json{{"error", "access violation at " + JsonHelpers::address_to_string(addr)}});
        }
    }

    return json{
        {"address", JsonHelpers::address_to_string(address)},
        {"type", type}, {"count", count}, {"stride", stride},
        {"values", values}
    };
}

json get_camera() {
    auto& api = API::get();
    if (!api) return json{{"error", "API not available"}};

    auto controller = api->get_player_controller(0);
    if (!controller) return json{{"error", "No player controller"}};

    auto cls = controller->get_class();
    if (!cls) return json{{"error", "Controller has no class"}};

    // Find PlayerCameraManager
    auto cam_prop = cls->find_property(L"PlayerCameraManager");
    if (!cam_prop) return json{{"error", "PlayerCameraManager property not found"}};

    auto cam_fprop = reinterpret_cast<API::FProperty*>(cam_prop);
    auto cam_addr = reinterpret_cast<uintptr_t>(controller) + cam_fprop->get_offset();
    auto cam_manager = *reinterpret_cast<API::UObject**>(cam_addr);

    if (!cam_manager) return json{{"error", "PlayerCameraManager is null"}};

    json result;
    result["cameraManager"]["address"] = JsonHelpers::address_to_string(cam_manager);
    auto cam_cls = cam_manager->get_class();
    if (cam_cls && cam_cls->get_fname()) {
        result["cameraManager"]["className"] = JsonHelpers::wide_to_utf8(cam_cls->get_fname()->to_string());
    }

    if (!cam_cls) return result;

    // Helper to call a getter method on the camera manager
    auto try_getter = [&](const wchar_t* method_name) -> json {
        auto func = cam_cls->find_function(method_name);
        if (!func) return nullptr;

        // Check for 0 required params
        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
            auto fp = reinterpret_cast<API::FProperty*>(p);
            if (fp->is_param() && !fp->is_return_param()) return nullptr;
        }

        auto ps = func->get_properties_size();
        auto ma = func->get_min_alignment();
        std::vector<uint8_t> params;
        if (ma > 1) params.resize(((ps + ma - 1) / ma) * ma, 0);
        else params.resize(ps, 0);

        cam_manager->process_event(func, params.data());

        for (auto p = func->get_child_properties(); p; p = p->get_next()) {
            auto fp = reinterpret_cast<API::FProperty*>(p);
            if (!fp->is_return_param()) continue;
            auto pc = fp->get_class();
            if (pc && pc->get_fname()->to_string() == L"BoolProperty") {
                return json(reinterpret_cast<API::FBoolProperty*>(fp)->get_value_from_object(params.data()));
            }
            return PropertyReader::read_property(params.data(), fp, 2);
        }
        return nullptr;
    };

    // Try standard UE camera methods
    auto loc = try_getter(L"GetCameraLocation");
    if (!loc.is_null()) result["position"] = loc;

    auto rot = try_getter(L"GetCameraRotation");
    if (!rot.is_null()) result["rotation"] = rot;

    auto fov = try_getter(L"GetFOVAngle");
    if (!fov.is_null()) result["fov"] = fov;

    // Also try K2_ variants (Blueprint-exposed versions)
    if (loc.is_null()) {
        loc = try_getter(L"K2_GetActorLocation");
        if (!loc.is_null()) result["position"] = loc;
    }

    // Read useful fields from CameraManager if methods didn't work
    if (result.find("fov") == result.end()) {
        auto fov_prop = cam_cls->find_property(L"DefaultFOV");
        if (fov_prop) {
            auto fp = reinterpret_cast<API::FProperty*>(fov_prop);
            result["fov"] = PropertyReader::read_property(cam_manager, fp, 0);
        }
    }

    return result;
}

} // namespace ObjectExplorer
