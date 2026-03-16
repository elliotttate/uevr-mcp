#pragma once

namespace httplib { class Server; }

namespace BlueprintRoutes {
    void register_routes(httplib::Server& server);
}
