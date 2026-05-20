#pragma once

#include <httplib.h>

namespace RenderRoutes {
    void register_routes(httplib::Server& server);
}
