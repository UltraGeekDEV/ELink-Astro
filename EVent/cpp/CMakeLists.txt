cmake_minimum_required(VERSION 3.16)
project(evn_cpp CXX)

# EVent for C++: a header-only library (include/evn), C++17, links the platform's threads.
#   add_subdirectory(path/to/this/folder)
#   target_link_libraries(my_app PRIVATE evn)
# Or just add include/ to the include path and link Threads (-pthread) yourself.
find_package(Threads REQUIRED)
add_library(evn INTERFACE)
target_include_directories(evn INTERFACE "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_compile_features(evn INTERFACE cxx_std_17)
target_link_libraries(evn INTERFACE Threads::Threads)
