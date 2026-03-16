#pragma once

namespace httplib { class Server; }

namespace AnimationRoutes {
    void register_routes(httplib::Server& server);
}
