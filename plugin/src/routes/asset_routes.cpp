#include "asset_routes.h"
#include "../game_thread_queue.h"
#include "../json_helpers.h"
#include "../pipe_server.h"
#include "../reflection/object_explorer.h"
#include "../reflection/function_caller.h"
#include "../reflection/property_reader.h"
#include "../reflection/property_writer.h"

#include <httplib.h>
#include <nlohmann/json.hpp>
#include <uevr/API.hpp>

using json = nlohmann::json;
using namespace uevr;

namespace AssetRoutes {

static void send_json(httplib::Response& res, const json& data, int status = 200) {
    if (data.contains("error") && status == 200) {
        auto err = data["error"].get<std::string>();
        status = (err.find("timeout") != std::string::npos) ? 504 :
                 (err.find("not found") != std::string::npos) ? 404 : 500;
    }
    res.status = status;
    res.set_content(data.dump(2), "application/json");
}

void register_routes(httplib::Server& server) {
    // GET /api/asset/find — Find a loaded asset by path
    server.Get("/api/asset/find", [](const httplib::Request& req, httplib::Response& res) {
        auto path = req.get_param_value("path");
        if (path.empty()) {
            send_json(res, json{{"error", "Missing 'path' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Asset: find " + path);

        auto result = GameThreadQueue::get().submit_and_wait([path]() -> json {
            auto& api = API::get();

            // Try to find the object directly by its path
            auto wpath = JsonHelpers::utf8_to_wide(path);
            auto* obj = api->find_uobject<API::UObject>(wpath.c_str());

            if (obj) {
                json response;
                response["found"] = true;
                response["address"] = JsonHelpers::address_to_string(obj);
                response["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

                auto* cls = obj->get_class();
                if (cls) {
                    auto* fname = cls->get_fname();
                    if (fname) {
                        response["class"] = JsonHelpers::fname_to_string(fname);
                    }
                }
                return response;
            }

            // If not found by exact path, try searching by the last path component
            std::string search_term = path;
            auto last_slash = path.rfind('/');
            if (last_slash != std::string::npos && last_slash + 1 < path.size()) {
                search_term = path.substr(last_slash + 1);
            }

            auto search_result = ObjectExplorer::search_objects(search_term, 10);
            if (search_result.contains("objects") && search_result["objects"].is_array() && !search_result["objects"].empty()) {
                // Look for the best match — one whose full name contains the original path
                for (const auto& entry : search_result["objects"]) {
                    if (entry.contains("fullName")) {
                        auto full_name = entry["fullName"].get<std::string>();
                        if (full_name.find(search_term) != std::string::npos) {
                            json response;
                            response["found"] = true;
                            response["address"] = entry.value("address", "");
                            response["fullName"] = full_name;
                            response["class"] = entry.value("class", "");
                            response["note"] = "Found via search (exact path not found)";
                            return response;
                        }
                    }
                }

                // Return the first result if no better match
                auto& first = search_result["objects"][0];
                json response;
                response["found"] = true;
                response["address"] = first.value("address", "");
                response["fullName"] = first.value("fullName", "");
                response["class"] = first.value("class", "");
                response["note"] = "Closest match from search (exact path not found)";
                return response;
            }

            return json{{"found", false}, {"error", "Asset not found: " + path}};
        }, 10000);

        send_json(res, result);
    });

    // GET /api/asset/search — Search loaded assets by name and optionally by type
    server.Get("/api/asset/search", [](const httplib::Request& req, httplib::Response& res) {
        auto query = req.get_param_value("query");
        auto type = req.get_param_value("type");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        if (query.empty() && type.empty()) {
            send_json(res, json{{"error", "Missing 'query' or 'type' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Asset: search query=" + query + " type=" + type);

        auto result = GameThreadQueue::get().submit_and_wait([query, type, limit]() -> json {
            // If type is specified, find objects of that class
            if (!type.empty()) {
                auto class_result = ObjectExplorer::find_objects_by_class(type, limit);

                // If query is also specified, filter the results
                if (!query.empty() && class_result.contains("objects") && class_result["objects"].is_array()) {
                    json filtered = json::array();
                    std::string query_lower = query;
                    std::transform(query_lower.begin(), query_lower.end(), query_lower.begin(), ::tolower);

                    for (const auto& obj : class_result["objects"]) {
                        std::string name = "";
                        if (obj.contains("fullName")) {
                            name = obj["fullName"].get<std::string>();
                        } else if (obj.contains("name")) {
                            name = obj["name"].get<std::string>();
                        }
                        std::string name_lower = name;
                        std::transform(name_lower.begin(), name_lower.end(), name_lower.begin(), ::tolower);

                        if (name_lower.find(query_lower) != std::string::npos) {
                            filtered.push_back(obj);
                            if (static_cast<int>(filtered.size()) >= limit) break;
                        }
                    }

                    return json{
                        {"assets", filtered},
                        {"count", filtered.size()},
                        {"query", query},
                        {"type", type}
                    };
                }

                // Return all objects of this type
                json response;
                response["type"] = type;
                if (class_result.contains("objects")) {
                    response["assets"] = class_result["objects"];
                    response["count"] = class_result["objects"].size();
                } else {
                    response["assets"] = json::array();
                    response["count"] = 0;
                    if (class_result.contains("error")) {
                        response["error"] = class_result["error"];
                    }
                }
                return response;
            }

            // General search by name
            auto search_result = ObjectExplorer::search_objects(query, limit);
            json response;
            if (search_result.contains("objects")) {
                response["assets"] = search_result["objects"];
                response["count"] = search_result["objects"].size();
            } else {
                response["assets"] = json::array();
                response["count"] = 0;
            }
            response["query"] = query;
            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/asset/load — Attempt to load an asset by path
    server.Post("/api/asset/load", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto path = body.value("path", "");
        auto class_path = body.value("class", "");

        if (path.empty()) {
            send_json(res, json{{"error", "Missing 'path'"}}, 400);
            return;
        }

        PipeServer::get().log("Asset: load " + path);

        auto result = GameThreadQueue::get().submit_and_wait([path, class_path]() -> json {
            auto& api = API::get();

            // First, try to find the object directly
            auto wpath = JsonHelpers::utf8_to_wide(path);
            auto* obj = api->find_uobject<API::UObject>(wpath.c_str());

            if (obj) {
                json response;
                response["loaded"] = true;
                response["address"] = JsonHelpers::address_to_string(obj);
                response["fullName"] = JsonHelpers::wide_to_utf8(obj->get_full_name());

                auto* cls = obj->get_class();
                if (cls) {
                    auto* fname = cls->get_fname();
                    if (fname) {
                        response["class"] = JsonHelpers::fname_to_string(fname);
                    }
                }

                auto* obj_fname = obj->get_fname();
                if (obj_fname) {
                    response["name"] = JsonHelpers::fname_to_string(obj_fname);
                }
                return response;
            }

            // If class is specified, try finding the class and then search for matching objects
            if (!class_path.empty()) {
                auto wclass = JsonHelpers::utf8_to_wide(class_path);
                auto* target_cls = api->find_uobject<API::UClass>(wclass.c_str());
                if (!target_cls) {
                    // Try with "Class " prefix
                    target_cls = api->find_uobject<API::UClass>(L"Class " + wclass);
                }

                if (target_cls) {
                    // Extract the asset name from the path for searching
                    std::string search_term = path;
                    auto last_slash = path.rfind('/');
                    if (last_slash != std::string::npos && last_slash + 1 < path.size()) {
                        search_term = path.substr(last_slash + 1);
                    }
                    // Remove extension if present
                    auto dot_pos = search_term.rfind('.');
                    if (dot_pos != std::string::npos) {
                        search_term = search_term.substr(0, dot_pos);
                    }

                    auto class_name = JsonHelpers::wide_to_utf8(target_cls->get_fname()->to_string());
                    auto class_result = ObjectExplorer::find_objects_by_class(class_name, 100);

                    if (class_result.contains("objects") && class_result["objects"].is_array()) {
                        std::string search_lower = search_term;
                        std::transform(search_lower.begin(), search_lower.end(), search_lower.begin(), ::tolower);

                        for (const auto& entry : class_result["objects"]) {
                            std::string full_name = entry.value("fullName", "");
                            std::string full_lower = full_name;
                            std::transform(full_lower.begin(), full_lower.end(), full_lower.begin(), ::tolower);

                            if (full_lower.find(search_lower) != std::string::npos) {
                                json response;
                                response["loaded"] = true;
                                response["address"] = entry.value("address", "");
                                response["fullName"] = full_name;
                                response["class"] = entry.value("class", "");
                                response["name"] = search_term;
                                response["note"] = "Found matching object of specified class";
                                return response;
                            }
                        }
                    }
                }
            }

            // Try a general search as last resort
            std::string search_term = path;
            auto last_slash = path.rfind('/');
            if (last_slash != std::string::npos && last_slash + 1 < path.size()) {
                search_term = path.substr(last_slash + 1);
            }
            auto dot_pos = search_term.rfind('.');
            if (dot_pos != std::string::npos) {
                search_term = search_term.substr(0, dot_pos);
            }

            auto search_result = ObjectExplorer::search_objects(search_term, 10);
            if (search_result.contains("objects") && search_result["objects"].is_array() && !search_result["objects"].empty()) {
                auto& first = search_result["objects"][0];
                json response;
                response["loaded"] = true;
                response["address"] = first.value("address", "");
                response["fullName"] = first.value("fullName", "");
                response["class"] = first.value("class", "");
                response["name"] = search_term;
                response["note"] = "Found via search — may not be the exact asset requested";
                return response;
            }

            return json{
                {"loaded", false},
                {"error", "Asset not found in memory. It may need to be loaded by the game first."},
                {"path", path}
            };
        }, 10000);

        send_json(res, result);
    });

    // GET /api/asset/classes — List loaded asset types (classes that have instances)
    server.Get("/api/asset/classes", [](const httplib::Request& req, httplib::Response& res) {
        auto filter = req.get_param_value("filter");
        int limit = 50;
        if (req.has_param("limit")) {
            try { limit = std::stoi(req.get_param_value("limit")); } catch (...) {}
        }

        if (filter.empty()) {
            send_json(res, json{{"error", "Missing 'filter' parameter"}}, 400);
            return;
        }

        PipeServer::get().log("Asset: list classes filter=" + filter);

        auto result = GameThreadQueue::get().submit_and_wait([filter, limit]() -> json {
            auto class_result = ObjectExplorer::search_classes(filter, limit);

            json response;
            if (class_result.contains("classes") && class_result["classes"].is_array()) {
                json classes = json::array();
                for (const auto& entry : class_result["classes"]) {
                    json cls_info;
                    cls_info["address"] = entry.value("address", "");
                    cls_info["name"] = entry.value("name", entry.value("fullName", ""));
                    if (entry.contains("fullName")) {
                        cls_info["fullName"] = entry["fullName"];
                    }

                    // Try to get instance count by finding objects of this class
                    if (entry.contains("name")) {
                        auto instances = ObjectExplorer::find_objects_by_class(entry["name"].get<std::string>(), 1);
                        if (instances.contains("count")) {
                            cls_info["instanceCount"] = instances["count"];
                        } else if (instances.contains("objects") && instances["objects"].is_array()) {
                            cls_info["instanceCount"] = instances["objects"].size();
                        }
                    }

                    classes.push_back(cls_info);
                }
                response["classes"] = classes;
                response["count"] = classes.size();
            } else if (class_result.contains("objects") && class_result["objects"].is_array()) {
                // search_classes might return under "objects" key
                json classes = json::array();
                for (const auto& entry : class_result["objects"]) {
                    json cls_info;
                    cls_info["address"] = entry.value("address", "");
                    cls_info["name"] = entry.value("name", entry.value("fullName", ""));
                    if (entry.contains("fullName")) {
                        cls_info["fullName"] = entry["fullName"];
                    }
                    classes.push_back(cls_info);
                }
                response["classes"] = classes;
                response["count"] = classes.size();
            } else {
                response["classes"] = json::array();
                response["count"] = 0;
                if (class_result.contains("error")) {
                    response["error"] = class_result["error"];
                }
            }

            response["filter"] = filter;
            return response;
        }, 10000);

        send_json(res, result);
    });

    // POST /api/asset/load_class — Load a class by name
    server.Post("/api/asset/load_class", [](const httplib::Request& req, httplib::Response& res) {
        json body;
        try { body = json::parse(req.body); } catch (...) {
            send_json(res, json{{"error", "Invalid JSON body"}}, 400);
            return;
        }

        auto class_name = body.value("className", "");
        if (class_name.empty()) {
            send_json(res, json{{"error", "Missing 'className'"}}, 400);
            return;
        }

        PipeServer::get().log("Asset: load_class " + class_name);

        auto result = GameThreadQueue::get().submit_and_wait([class_name]() -> json {
            auto& api = API::get();

            // Try to find the class directly
            auto wname = JsonHelpers::utf8_to_wide(class_name);
            auto* cls = api->find_uobject<API::UClass>(wname.c_str());

            if (!cls) {
                // Try with "Class " prefix
                cls = api->find_uobject<API::UClass>((L"Class " + wname).c_str());
            }

            if (!cls) {
                // Try with /Script/Engine prefix
                auto with_prefix = L"Class /Script/Engine." + wname;
                cls = api->find_uobject<API::UClass>(with_prefix.c_str());
            }

            if (cls) {
                json response;
                response["found"] = true;
                response["address"] = JsonHelpers::address_to_string(cls);

                auto* fname = cls->get_fname();
                if (fname) {
                    response["name"] = JsonHelpers::fname_to_string(fname);
                }

                response["fullName"] = JsonHelpers::wide_to_utf8(reinterpret_cast<API::UObject*>(cls)->get_full_name());

                // Get super class info
                auto* super = cls->get_super();
                if (super) {
                    auto* super_cls = reinterpret_cast<API::UStruct*>(super);
                    auto* super_obj = reinterpret_cast<API::UObject*>(super_cls);
                    auto* super_fname = super_obj->get_fname();
                    if (super_fname) {
                        response["superClass"] = JsonHelpers::fname_to_string(super_fname);
                    }
                }

                // Check if CDO exists
                auto* cdo = cls->get_class_default_object();
                if (cdo) {
                    response["cdoAddress"] = JsonHelpers::address_to_string(cdo);
                }

                return response;
            }

            // Try searching as a fallback
            auto search_result = ObjectExplorer::search_classes(class_name, 5);
            if (search_result.contains("classes") && search_result["classes"].is_array() && !search_result["classes"].empty()) {
                auto& first = search_result["classes"][0];
                json response;
                response["found"] = true;
                response["address"] = first.value("address", "");
                response["name"] = first.value("name", "");
                if (first.contains("fullName")) {
                    response["fullName"] = first["fullName"];
                }
                response["note"] = "Found via class search (exact name match failed)";
                return response;
            } else if (search_result.contains("objects") && search_result["objects"].is_array() && !search_result["objects"].empty()) {
                auto& first = search_result["objects"][0];
                json response;
                response["found"] = true;
                response["address"] = first.value("address", "");
                response["name"] = first.value("name", first.value("fullName", ""));
                response["note"] = "Found via class search (exact name match failed)";
                return response;
            }

            return json{{"found", false}, {"error", "Class not found: " + class_name}};
        }, 10000);

        send_json(res, result);
    });
}

} // namespace AssetRoutes
