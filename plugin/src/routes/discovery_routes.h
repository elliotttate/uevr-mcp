#pragma once

namespace httplib { class Server; }

namespace DiscoveryRoutes {
    void register_routes(httplib::Server& server);
}
