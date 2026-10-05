// A C++ leaf: joins the router of examples/csharp/Hello, fires a reading, publishes a stream, calls Home.Count.
#include <evn/leaf.hpp>
#include <iostream>

struct Reading {
    float Celsius = 0;
    std::string Room;
};
EVN_RECORD(Reading, Celsius, Room)
EVN_DOCS(Reading, evn::doc("Celsius", "degrees Celsius").range(-40, 125))

int main() {
    evn::Leaf leaf("sensor");
    if (!leaf.connect_tcp("127.0.0.1", 5698)) {
        std::cerr << "no router on 127.0.0.1:5698 (run examples/csharp/Hello first)\n";
        return 1;
    }
    // The type must match what the network agreed (Reading{Celsius:float,Room:string}).
    bool delivered = leaf.fire("Home.Reading", Reading{21.5f, "den"});
    std::cout << "fired: " << (delivered ? "delivered" : "refused (type mismatch)") << "\n";

    // A stream: publish is fire and forget (no acknowledgements; in order). The router prints every 1000th tick.
    for (int32_t i = 1; i <= 3000; i++) leaf.publish("Home.Tick", i);
    std::cout << "published 3000 ticks\n";

    auto counts = leaf.call<int32_t>("Home.Count", evn::Void{});  // one answer per provider
    if (counts)
        for (int32_t c : *counts) std::cout << "Home.Count answered " << c << "\n";
    else
        std::cout << "Home.Count: type mismatch\n";
}
