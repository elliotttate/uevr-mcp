#pragma once

namespace httplib { class Server; }

namespace ConsoleRoutes {
    void register_routes(httplib::Server& server);
}
