#pragma once

namespace httplib { class Server; }

namespace VrRoutes {
    void register_routes(httplib::Server& server);
}
