cmake_minimum_required(VERSION 3.16)
project(evn_example CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The library lives next to the examples in the package folder: dist/EVent/cpp
add_subdirectory(../../cpp evn)

add_executable(sensor sensor.cpp)
target_link_libraries(sensor PRIVATE evn)
