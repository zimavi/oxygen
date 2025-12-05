cmake_minimum_required(VERSION 3.31.2)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Link libraries statically")

if(CMAKE_CXX_COMPILER_ID MATCHES "CNU|Clang")
	set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -static -static-libgcc -static-libstdc++")
	
	set(CMAKE_FIND_LIBRARY_SUFFIXES .a)
endif()

if(MSVC)
	set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

#add_subdirectory("thirdparty/lib")

project(oxygen LANGUAGES CXX)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

file(GLOB_RECURSE MY_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp")

add_executable(oxygen "${MY_SOURCES}")

target_compile_definitions("${CMAKE_PROJECT_NAME}" PUBLIC RESOURCE_PATH="${CMAKE_CURRENT_SOURCE_DIR}/resources/")
#target_compile_definitions("${CMAKE_PROJECT_NAME}" PUBLIC RESOURCE_PATH="./resources/")

target_include_directories(oxygen PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include/")
#target_link_libraries(oxygen PRIVATE lib)

# ----- LLVM ------
find_package(LLVM REQUIRED CONFIG)

message(STATUS "Found LLVM ${LLVM_PACKAGE_VERSION}")
message(STATUS "LLVM_DIR = ${LLVM_DIR}")

if (DEFINED LLVM_INCLUDE_DIRS)
    target_include_directories(oxygen PRIVATE ${LLVM_INCLUDE_DIRS})
endif()
if (DEFINED LLVM_DEFINITIONS)
    target_compile_definitions(oxygen PRIVATE ${LLVM_DEFINITIONS})
endif()

llvm_map_components_to_libnames(REQ_LLVM_LIBS
    core
    support
    native
)

target_link_libraries(oxygen PRIVATE ${REQ_LLVM_LIBS})

