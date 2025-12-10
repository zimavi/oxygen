#pragma once

#include <fstream>
#include <map>
#include <sstream>
#include <string>
#include <utility>
#include <vector>

class SourceManager {
public:
    struct FileInfo {
        std::string path;
        std::vector<std::string> lines;
        std::string content;
    };

    int loadFile(const std::string& path) {
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

    const FileInfo* getFile(int fileID) const {
        auto it = files.find(fileID);
        return (it == files.end() ? nullptr : &it->second);
    }

    const std::vector<std::string>& getLines(int fileID) const {
        static const std::vector<std::string> empty;
        auto it = files.find(fileID);
        return it != files.end() ? it->second.lines : empty;
    }

    const std::string& getContent(int fileID) const {
        static const std::string empty;
        auto it = files.find(fileID);
        return it != files.end() ? it->second.content : empty;
    }

    bool exists(int fileID) const {
        auto it = files.find(fileID);
        return it != files.end();
    }

private:
    int nextID = 1;
    std::map<int, FileInfo> files;
    std::map<std::string, int> fileIDs;
};
