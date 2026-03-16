#pragma once
namespace httplib { class Server; }
namespace HookRoutes {
    void register_routes(httplib::Server& server);
}
