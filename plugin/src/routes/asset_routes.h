#pragma once

namespace httplib { class Server; }

namespace AssetRoutes {
    void register_routes(httplib::Server& server);
}
