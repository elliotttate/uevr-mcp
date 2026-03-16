#pragma once

namespace httplib { class Server; }

namespace ExplorerRoutes {
    void register_routes(httplib::Server& server);
}
