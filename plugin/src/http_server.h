#pragma once

#include <thread>
#include <atomic>
#include <functional>
#include <string>

namespace httplib { class Server; }

class HttpServer {
public:
    static HttpServer& get() {
        static HttpServer instance;
        return instance;
    }

    // Start the HTTP server on a background thread. Non-blocking.
    bool start(int port = 8899);

    // Stop the server and join the thread.
    void stop();

    bool is_running() const { return m_running.load(); }
    int port() const { return m_port; }

private:
    HttpServer();
    ~HttpServer();

    void register_routes();
    void server_thread_func();

    std::unique_ptr<httplib::Server> m_server;
    std::thread m_thread;
    std::atomic<bool> m_running{false};
    int m_port{8899};
};
