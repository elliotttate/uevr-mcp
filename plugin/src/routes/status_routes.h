#pragma once

#include <cstdint>

namespace httplib { class Server; }

namespace StatusRoutes {
    void register_routes(httplib::Server& server);
    void increment_tick_count();
    std::uint64_t get_tick_count();
}
