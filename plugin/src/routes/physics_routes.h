#pragma once

namespace httplib { class Server; }

namespace PhysicsRoutes {
    void register_routes(httplib::Server& server);
}
