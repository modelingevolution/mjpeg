cmake_minimum_required(VERSION 3.20)
project(LibJpegWrap LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find libjpeg-turbo (or standard libjpeg)
find_package(JPEG REQUIRED)

# Build shared library
add_library(LibJpegWrap SHARED LibJpegWrap.cpp)

target_include_directories(LibJpegWrap PRIVATE ${JPEG_INCLUDE_DIR})
target_link_libraries(LibJpegWrap PRIVATE ${JPEG_LIBRARIES})

# Platform-specific settings
if(WIN32)
    set_target_properties(LibJpegWrap PROPERTIES
        OUTPUT_NAME "LibJpegWrap"
        SUFFIX ".dll"
    )
elseif(UNIX)
    set_target_properties(LibJpegWrap PROPERTIES
        OUTPUT_NAME "LibJpegWrap"
        SUFFIX ".so"
        POSITION_INDEPENDENT_CODE ON
    )
endif()

# Install rules
install(TARGETS LibJpegWrap
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
)
