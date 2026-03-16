#pragma once

namespace httplib { class Server; }

namespace MaterialRoutes {
    void register_routes(httplib::Server& server);
}
