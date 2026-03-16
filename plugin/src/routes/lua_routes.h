#pragma once

namespace httplib { class Server; }

namespace LuaRoutes {
    void register_routes(httplib::Server& server);
}
