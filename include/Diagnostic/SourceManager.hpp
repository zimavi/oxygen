#pragma once

#include <map>
#include <string>
#include <vector>

class SourceManager {
public:
    struct FileInfo {
        std::string path;
        std::vector<std::string> lines;
        std::string content;
    };

    int loadFile(const std::string& path);

    const FileInfo* getFile(int fileID) const;

    const std::vector<std::string>& getLines(int fileID) const;

    const std::string& getContent(int fileID) const;

    bool exists(int fileID) const;

private:
    int nextID = 1;
    std::map<int, FileInfo> files;
    std::map<std::string, int> fileIDs;
};
