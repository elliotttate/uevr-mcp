#pragma once
namespace httplib { class Server; }
namespace MacroRoutes {
    void register_routes(httplib::Server& server);
}
