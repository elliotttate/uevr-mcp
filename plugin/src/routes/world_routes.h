#pragma once

namespace httplib { class Server; }

namespace WorldRoutes {
    void register_routes(httplib::Server& server);
}
