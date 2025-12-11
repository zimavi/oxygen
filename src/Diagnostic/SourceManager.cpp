#include <fstream>
#include <sstream>
#include <utility>
#include "Diagnostic/SourceManager.hpp"


int SourceManager::loadFile(const std::string& path) {
    if (fileIDs.count(path)) {
        return fileIDs[path];
    }

    std::ifstream ifs(path);
    if (!ifs) return -1;

    FileInfo info;
    info.path = path;

    std::ostringstream ss;
    ss << ifs.rdbuf();
    info.content = ss.str();

    std::string line;
    std::istringstream ls(info.content);
    while (std::getline(ls, line)) {
        info.lines.push_back(line);
    }

    int id = nextID++;
    files[id] = std::move(info);

    return id;
}


const SourceManager::FileInfo* SourceManager::getFile(int fileID) const {
    auto it = files.find(fileID);
    return (it == files.end() ? nullptr : &it->second);
}

const std::vector<std::string>& SourceManager::getLines(int fileID) const {
    static const std::vector<std::string> empty;
    auto it = files.find(fileID);
    return it != files.end() ? it->second.lines : empty;
}

const std::string& SourceManager::getContent(int fileID) const {
    static const std::string empty;
    auto it = files.find(fileID);
    return it != files.end() ? it->second.content : empty;
}

bool SourceManager::exists(int fileID) const {
    auto it = files.find(fileID);
    return it != files.end();
}
