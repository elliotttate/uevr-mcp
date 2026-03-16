#pragma once

namespace httplib { class Server; }

namespace WatchRoutes {
    void register_routes(httplib::Server& server);
}
