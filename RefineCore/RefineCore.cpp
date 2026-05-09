#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wininet.h>
#include <string>
#include <sstream>
#include <array>
#include <thread>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <functional>
#include <mutex>

#pragma comment(lib, "wininet.lib")

#include "json.hpp"
using namespace std;
using json = nlohmann::json;
namespace fs = std::filesystem;

// ─────────────────────────────────────────────────────────────────────────────
// Pipe
// ─────────────────────────────────────────────────────────────────────────────

static HANDLE g_pipe = INVALID_HANDLE_VALUE;
static std::atomic<bool> g_running{ true };
static std::mutex g_sendMutex;

static void logDebug(const std::string& msg); // defined below, after HTTP helpers

static void sendMessage(const json& msg)
{
    std::string data = msg.dump() + "\n";
    DWORD written = 0;
    std::lock_guard<std::mutex> lk(g_sendMutex);
    if (!WriteFile(g_pipe, data.c_str(), static_cast<DWORD>(data.size()), &written, nullptr))
        logDebug("sendMessage WriteFile FAILED err=" + std::to_string(GetLastError())
                 + " msg=" + data);
}

static std::string readLine()
{
    // Never call blocking ReadFile while other threads need to WriteFile on the
    // same synchronous handle — Windows serialises I/O at the file-object level,
    // causing a deadlock (download thread's WriteFile waits for ReadFile to
    // release the lock; ReadFile waits for WPF to send data; WPF waits for C++
    // to send progress — nobody moves).  PeekNamedPipe is non-blocking, so
    // ReadFile is only called when data is already in the buffer and returns
    // immediately, keeping the lock window to microseconds.
    std::string line;
    char ch = 0;
    DWORD bytesRead = 0;
    while (g_running)
    {
        DWORD avail = 0;
        if (!PeekNamedPipe(g_pipe, nullptr, 0, nullptr, &avail, nullptr))
            return {};  // pipe broken
        if (avail == 0)
        {
            Sleep(10);
            continue;
        }
        if (!ReadFile(g_pipe, &ch, 1, &bytesRead, nullptr) || bytesRead == 0)
            return {};
        if (ch == '\n') break;
        line += ch;
    }
    return line;
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP (WinINet)
// ─────────────────────────────────────────────────────────────────────────────

static bool httpGet(const std::string& host, const std::string& path, std::string& outBody)
{
    HINTERNET hNet = InternetOpenA("RefineCore/1.0",
        INTERNET_OPEN_TYPE_PRECONFIG,
        nullptr, nullptr, 0);
    if (!hNet) return false;

    HINTERNET hConn = InternetConnectA(hNet, host.c_str(),
        INTERNET_DEFAULT_HTTPS_PORT,
        nullptr, nullptr,
        INTERNET_SERVICE_HTTP, 0, 0);
    if (!hConn) { InternetCloseHandle(hNet); return false; }

    DWORD flags = INTERNET_FLAG_SECURE
        | INTERNET_FLAG_RELOAD
        | INTERNET_FLAG_NO_CACHE_WRITE;

    HINTERNET hReq = HttpOpenRequestA(hConn, "GET", path.c_str(),
        nullptr, nullptr, nullptr, flags, 0);
    if (!hReq)
    {
        InternetCloseHandle(hConn);
        InternetCloseHandle(hNet);
        return false;
    }

    if (!HttpSendRequestA(hReq, nullptr, 0, nullptr, 0))
    {
        InternetCloseHandle(hReq);
        InternetCloseHandle(hConn);
        InternetCloseHandle(hNet);
        return false;
    }

    char buf[4096];
    DWORD read = 0;
    while (InternetReadFile(hReq, buf, sizeof(buf), &read) && read > 0)
        outBody.append(buf, read);

    InternetCloseHandle(hReq);
    InternetCloseHandle(hConn);
    InternetCloseHandle(hNet);
    return true;
}

static bool httpDownloadFile(
    const std::string& url,
    const std::string& destPath,
    std::function<void(uint64_t, uint64_t)> progressCb)
{
    // Split https://host/path
    size_t schemeEnd = url.find("://");
    if (schemeEnd == std::string::npos) return false;
    std::string rest = url.substr(schemeEnd + 3);
    size_t slashPos = rest.find('/');
    if (slashPos == std::string::npos) return false;
    std::string host = rest.substr(0, slashPos);
    std::string path = rest.substr(slashPos);

    HINTERNET hNet = InternetOpenA("RefineCore/1.0",
        INTERNET_OPEN_TYPE_PRECONFIG,
        nullptr, nullptr, 0);
    if (!hNet) return false;

    HINTERNET hConn = InternetConnectA(hNet, host.c_str(),
        INTERNET_DEFAULT_HTTPS_PORT,
        nullptr, nullptr,
        INTERNET_SERVICE_HTTP, 0, 0);
    if (!hConn) { InternetCloseHandle(hNet); return false; }

    DWORD flags = INTERNET_FLAG_SECURE
        | INTERNET_FLAG_RELOAD
        | INTERNET_FLAG_NO_CACHE_WRITE
        | INTERNET_FLAG_IGNORE_REDIRECT_TO_HTTPS;

    HINTERNET hReq = HttpOpenRequestA(hConn, "GET", path.c_str(),
        nullptr, nullptr, nullptr, flags, 0);
    if (!hReq)
    {
        InternetCloseHandle(hConn);
        InternetCloseHandle(hNet);
        return false;
    }

    // Follow redirects (GitHub releases redirect to CDN)
    BOOL sent = FALSE;
    for (int redirect = 0; redirect < 5 && !sent; ++redirect)
    {
        sent = HttpSendRequestA(hReq, nullptr, 0, nullptr, 0);
        if (!sent) break;

        DWORD status = 0;
        DWORD statusSize = sizeof(status);
        DWORD idx = 0;
        HttpQueryInfoA(hReq,
            HTTP_QUERY_STATUS_CODE | HTTP_QUERY_FLAG_NUMBER,
            &status, &statusSize, &idx);

        if (status == 301 || status == 302 || status == 303 || status == 307)
        {
            char location[2048]{};
            DWORD locSize = sizeof(location);
            idx = 0;
            if (HttpQueryInfoA(hReq, HTTP_QUERY_LOCATION,
                location, &locSize, &idx))
            {
                // Re-open request to new location
                InternetCloseHandle(hReq);
                InternetCloseHandle(hConn);

                std::string newUrl(location);
                size_t se = newUrl.find("://");
                std::string newRest = newUrl.substr(se + 3);
                size_t sp = newRest.find('/');
                std::string newHost = newRest.substr(0, sp);
                std::string newPath = newRest.substr(sp);

                hConn = InternetConnectA(hNet, newHost.c_str(),
                    INTERNET_DEFAULT_HTTPS_PORT,
                    nullptr, nullptr,
                    INTERNET_SERVICE_HTTP, 0, 0);
                hReq = HttpOpenRequestA(hConn, "GET", newPath.c_str(),
                    nullptr, nullptr, nullptr, flags, 0);
                sent = FALSE;
            }
        }
        else break;
    }

    if (!sent)
    {
        InternetCloseHandle(hReq);
        InternetCloseHandle(hConn);
        InternetCloseHandle(hNet);
        return false;
    }

    // Content-Length
    uint64_t totalBytes = 0;
    char lenBuf[32]{};
    DWORD lenSize = sizeof(lenBuf);
    DWORD idx = 0;
    if (HttpQueryInfoA(hReq, HTTP_QUERY_CONTENT_LENGTH, lenBuf, &lenSize, &idx))
        totalBytes = std::stoull(lenBuf);

    std::ofstream file(destPath, std::ios::binary);
    if (!file.is_open())
    {
        InternetCloseHandle(hReq);
        InternetCloseHandle(hConn);
        InternetCloseHandle(hNet);
        return false;
    }

    char buf[65536];
    DWORD read = 0;
    uint64_t soFar = 0;
    while (InternetReadFile(hReq, buf, sizeof(buf), &read) && read > 0)
    {
        file.write(buf, read);
        soFar += read;
        if (progressCb) progressCb(soFar, totalBytes);
    }

    file.close();
    InternetCloseHandle(hReq);
    InternetCloseHandle(hConn);
    InternetCloseHandle(hNet);
    return soFar > 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// GitHub latest release asset resolver
// ─────────────────────────────────────────────────────────────────────────────

struct GitHubAsset
{
    std::string name;
    std::string downloadUrl;
};

static bool getLatestAsset(const std::string& owner,
    const std::string& repo,
    const std::string& nameContains,
    const std::string& nameExact,   // "" = ignore
    GitHubAsset& out)
{
    std::string body;
    if (!httpGet("api.github.com",
        "/repos/" + owner + "/" + repo + "/releases/latest",
        body))
        return false;

    try
    {
        auto j = json::parse(body);
        for (auto& a : j.at("assets"))
        {
            std::string name = a.value("name", "");
            bool match = !nameExact.empty()
                ? (name == nameExact)
                : (name.find(nameContains) != std::string::npos);
            if (match)
            {
                out.name = name;
                out.downloadUrl = a.value("browser_download_url", "");
                return true;
            }
        }
    }
    catch (...) {}
    return false;
}

// ─────────────────────────────────────────────────────────────────────────────
// ZIP extract via PowerShell
// ─────────────────────────────────────────────────────────────────────────────

static bool extractZip(const std::string& zipPath, const std::string& destDir)
{
    std::string cmd =
        "powershell -NoProfile -Command "
        "\"Expand-Archive -Force -Path '" + zipPath +
        "' -DestinationPath '" + destDir + "'\"";

    STARTUPINFOA si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;

    PROCESS_INFORMATION pi{};
    if (!CreateProcessA(nullptr, const_cast<char*>(cmd.c_str()),
        nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return false;

    WaitForSingleObject(pi.hProcess, 120000);
    DWORD exitCode = 1;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return exitCode == 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Tools directory
// ─────────────────────────────────────────────────────────────────────────────

static std::string g_toolsDir;

static std::string getExeDir()
{
    char buf[MAX_PATH];
    GetModuleFileNameA(nullptr, buf, MAX_PATH);
    return fs::path(buf).parent_path().string();
}

static void sendSetup(const std::string& tool,
    const std::string& status,
    int percent = -1)
{
    json msg = { {"type","setup"}, {"tool",tool}, {"status",status} };
    if (percent >= 0) msg["percent"] = percent;
    sendMessage(msg);
}

// ─────────────────────────────────────────────────────────────────────────────
// Ensure yt-dlp
// ─────────────────────────────────────────────────────────────────────────────

static bool ensureYtDlp()
{
    std::string dest = g_toolsDir + "\\yt-dlp.exe";
    if (fs::exists(dest))
    {
        sendSetup("yt-dlp", "Already installed", 100);
        return true;
    }

    sendSetup("yt-dlp", "Fetching latest version…");

    GitHubAsset asset;
    bool found = getLatestAsset("yt-dlp", "yt-dlp", "", "yt-dlp.exe", asset);

    // Fallback to known direct URL if API fails
    if (!found || asset.downloadUrl.empty())
        asset.downloadUrl =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    sendSetup("yt-dlp", "Downloading…", 0);

    bool ok = httpDownloadFile(asset.downloadUrl, dest,
        [&](uint64_t recv, uint64_t total)
        {
            if (total > 0)
                sendSetup("yt-dlp", "Downloading…",
                    static_cast<int>((recv * 100) / total));
        });

    if (ok) sendSetup("yt-dlp", "Installed", 100);
    else    sendSetup("yt-dlp", "Download failed", -1);
    return ok;
}

// ─────────────────────────────────────────────────────────────────────────────
// Ensure ffmpeg
// ─────────────────────────────────────────────────────────────────────────────

static bool ensureFfmpeg()
{
    std::string dest = g_toolsDir + "\\ffmpeg.exe";
    if (fs::exists(dest))
    {
        sendSetup("ffmpeg", "Already installed", 100);
        return true;
    }

    sendSetup("ffmpeg", "Fetching latest version…");

    GitHubAsset asset;
    bool found = getLatestAsset("GyanD", "codexffmpeg",
        "essentials_build.zip", "", asset);

    if (!found || asset.downloadUrl.empty())
    {
        sendSetup("ffmpeg", "Failed to fetch release info");
        return false;
    }

    sendSetup("ffmpeg", "Downloading…", 0);

    std::string zipPath = g_toolsDir + "\\ffmpeg_temp.zip";
    bool ok = httpDownloadFile(asset.downloadUrl, zipPath,
        [&](uint64_t recv, uint64_t total)
        {
            if (total > 0)
                sendSetup("ffmpeg", "Downloading…",
                    static_cast<int>((recv * 100) / total));
        });

    if (!ok)
    {
        sendSetup("ffmpeg", "Download failed");
        return false;
    }

    sendSetup("ffmpeg", "Extracting…");

    std::string extractDir = g_toolsDir + "\\ffmpeg_extract";
    if (!extractZip(zipPath, extractDir))
    {
        sendSetup("ffmpeg", "Extraction failed");
        fs::remove(zipPath);
        return false;
    }

    // Walk extracted folder to find ffmpeg.exe
    bool copied = false;
    for (auto& entry : fs::recursive_directory_iterator(extractDir))
    {
        if (entry.path().filename() == "ffmpeg.exe")
        {
            fs::copy_file(entry.path(), dest,
                fs::copy_options::overwrite_existing);
            copied = true;
            break;
        }
    }

    fs::remove(zipPath);
    fs::remove_all(extractDir);

    if (copied) sendSetup("ffmpeg", "Installed", 100);
    else        sendSetup("ffmpeg", "Could not find ffmpeg.exe in archive");
    return copied;
}

// ─────────────────────────────────────────────────────────────────────────────
// ensureResources — called after pipe connects, before command loop
// ─────────────────────────────────────────────────────────────────────────────

static bool ensureResources()
{
    g_toolsDir = getExeDir() + "\\tools";
    fs::create_directories(g_toolsDir);

    bool ok = ensureYtDlp();
    ok = ensureFfmpeg() && ok;

    if (ok) sendMessage({ {"type","setup_complete"} });
    else    sendMessage({ {"type","setup_failed"} });

    return ok;
}

// ─────────────────────────────────────────────────────────────────────────────
// Download job
// ─────────────────────────────────────────────────────────────────────────────

struct DownloadJob
{
    std::string id;
    std::string url;
    std::string quality = "1080";
    bool        audioOnly = false;
    std::string format = "mp4";
    std::string outputDir;
};

static std::string buildFormatSelector(const DownloadJob& job)
{
    if (job.audioOnly) return "";
    if (job.quality == "best")
        return "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best";

    return "bestvideo[height<=" + job.quality + "][ext=mp4]"
        "+bestaudio[ext=m4a]"
        "/bestvideo[height<=" + job.quality + "]+bestaudio"
        "/best[height<=" + job.quality + "]";
}

static std::string buildCommand(const DownloadJob& job)
{
    std::string ytdlp = "\"" + g_toolsDir + "\\yt-dlp.exe\"";
    std::string ffmpeg = g_toolsDir + "\\ffmpeg.exe";

    std::ostringstream cmd;
    cmd << ytdlp;
    cmd << " --ffmpeg-location \"" << ffmpeg << "\"";
    cmd << " -o \"" << job.outputDir << "\\%(title)s.%(ext)s\"";
    cmd << " --retries 10 --fragment-retries 10 --retry-sleep 3";
    cmd << " --socket-timeout 30 --no-part --continue";
    cmd << " --newline";
    cmd << " --no-colors";           // ← stops ANSI escape codes blocking output
    cmd << " --no-warnings";         // ← reduces noise
    cmd << " --progress";            // ← force progress even when not a tty

    if (job.audioOnly)
    {
        cmd << " -x --audio-format " << job.format << " --audio-quality 0";
    }
    else
    {
        cmd << " -f \"" << buildFormatSelector(job) << "\"";
        cmd << " --merge-output-format " << job.format;
    }

    cmd << " \"" << job.url << "\"";
    return cmd.str();
}

static bool parseProgress(const std::string& line,
    float& outPct, std::string& outEta)
{
    if (line.find("[download]") == std::string::npos) return false;
    auto pctPos = line.find('%');
    if (pctPos == std::string::npos) return false;

    size_t start = pctPos;
    while (start > 0 &&
        (std::isdigit(line[start - 1]) || line[start - 1] == '.'))
        --start;

    try { outPct = std::stof(line.substr(start, pctPos - start)); }
    catch (...) { return false; }

    auto etaPos = line.find("ETA ");
    outEta = etaPos != std::string::npos
        ? line.substr(etaPos + 4, 5) : "--:--";
    return true;
}

// Add near the top of the file, after the includes
static void logDebug(const std::string& msg)
{
    std::string logPath = getExeDir() + "\\refinecore_debug.log";
    std::ofstream f(logPath, std::ios::app);
    f << msg << "\n";
    f.flush();
}

static void runDownload(DownloadJob job)
{
    logDebug("=== runDownload id=" + job.id);
    logDebug("url=" + job.url + " quality=" + job.quality
             + " audioOnly=" + std::to_string(job.audioOnly)
             + " format=" + job.format);
    logDebug("outputDir=" + job.outputDir);

    std::string cmdStr = buildCommand(job);
    logDebug("cmd=" + cmdStr);

    sendMessage({ {"type","started"}, {"id",job.id} });
    logDebug("sent started");

    std::vector<char> cmdBuf(cmdStr.begin(), cmdStr.end());
    cmdBuf.push_back('\0');

    // ── Security attributes: both pipe ends are inheritable by default ────────
    SECURITY_ATTRIBUTES sa{};
    sa.nLength        = sizeof(sa);
    sa.bInheritHandle = TRUE;

    // Give the child a real stdin handle.  Passing NULL here sets the child's
    // STD_INPUT_HANDLE to 0; the Python CRT sees an invalid handle during
    // startup and can crash or hang before writing a single byte of output.
    HANDLE hNul = CreateFileA("NUL", GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, NULL);
    logDebug("hNul=" + std::to_string(reinterpret_cast<uintptr_t>(hNul)));

    // ── Pipe captures child stdout + stderr ───────────────────────────────────
    HANDLE hReadPipe = nullptr, hWritePipe = nullptr;
    if (!CreatePipe(&hReadPipe, &hWritePipe, &sa, 0))
    {
        logDebug("CreatePipe FAILED err=" + std::to_string(GetLastError()));
        if (hNul != INVALID_HANDLE_VALUE) CloseHandle(hNul);
        sendMessage({ {"type","error"}, {"id",job.id},
                      {"message","CreatePipe failed"} });
        return;
    }
    // Our read end must NOT be inherited by the child
    SetHandleInformation(hReadPipe, HANDLE_FLAG_INHERIT, 0);
    logDebug("CreatePipe OK");

    // ── Launch yt-dlp ─────────────────────────────────────────────────────────
    STARTUPINFOA si{};
    si.cb          = sizeof(si);
    si.dwFlags     = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput  = hWritePipe;
    si.hStdError   = hWritePipe;
    si.hStdInput   = (hNul != INVALID_HANDLE_VALUE) ? hNul : INVALID_HANDLE_VALUE;

    PROCESS_INFORMATION pi{};
    BOOL ok = CreateProcessA(
        nullptr, cmdBuf.data(),
        nullptr, nullptr,
        TRUE,             // bInheritHandles — pipe write end and hNul pass through
        CREATE_NO_WINDOW,
        nullptr,          // inherit environment (PYTHONUNBUFFERED set at startup)
        nullptr,          // inherit working directory
        &si, &pi);

    // Close our copies of the child-side handles immediately after CreateProcess.
    // If we keep hWritePipe open, ReadFile on hReadPipe will never see EOF when
    // yt-dlp exits because our handle keeps the write end alive.
    CloseHandle(hWritePipe);
    if (hNul != INVALID_HANDLE_VALUE) CloseHandle(hNul);

    if (!ok)
    {
        DWORD err = GetLastError();
        logDebug("CreateProcess FAILED err=" + std::to_string(err));
        sendMessage({ {"type","error"}, {"id",job.id},
                      {"message","CreateProcess failed: " + std::to_string(err)} });
        CloseHandle(hReadPipe);
        return;
    }
    CloseHandle(pi.hThread);
    logDebug("CreateProcess OK pid=" + std::to_string(pi.dwProcessId));

    // ── Read output until yt-dlp closes its pipe end (process exit) ───────────
    // Blocking ReadFile is simpler and more reliable than PeekNamedPipe polling.
    // It returns FALSE with ERROR_BROKEN_PIPE once all write-end handles are
    // closed, which happens naturally when the child exits.
    char buf[4096];
    DWORD bytesRead;
    std::string lineBuffer;

    while (ReadFile(hReadPipe, buf, sizeof(buf), &bytesRead, nullptr)
           && bytesRead > 0)
    {
        lineBuffer.append(buf, bytesRead);

        size_t pos;
        while ((pos = lineBuffer.find('\n')) != std::string::npos)
        {
            std::string line = lineBuffer.substr(0, pos);
            lineBuffer.erase(0, pos + 1);
            if (!line.empty() && line.back() == '\r') line.pop_back();

            logDebug("ytdlp>> " + line);

            float pct = 0.0f;
            std::string eta;
            if (parseProgress(line, pct, eta))
                sendMessage({ {"type","progress"}, {"id",job.id},
                              {"percent",pct}, {"eta",eta} });
        }
    }
    logDebug("read loop done");

    CloseHandle(hReadPipe);

    WaitForSingleObject(pi.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess);

    logDebug("yt-dlp exit=" + std::to_string(exitCode));

    if (exitCode == 0)
        sendMessage({ {"type","finished"}, {"id",job.id} });
    else
        sendMessage({ {"type","error"}, {"id",job.id},
                      {"message","yt-dlp exited with code "
                                 + std::to_string(exitCode)} });
}

// ─────────────────────────────────────────────────────────────────────────────
// WinMain
// ─────────────────────────────────────────────────────────────────────────────

int WINAPI WinMain(
    _In_     HINSTANCE,
    _In_opt_ HINSTANCE,
    _In_     LPSTR,
    _In_     int)
{
    // 1. Create pipe first
    g_pipe = CreateNamedPipeA(
        "\\\\.\\pipe\\RefinePipe",
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1, 65536, 65536, 0, nullptr);

    if (g_pipe == INVALID_HANDLE_VALUE) return 1;

    // 2. Wait for WPF to connect
    if (!ConnectNamedPipe(g_pipe, nullptr))
    {
        CloseHandle(g_pipe);
        return 1;
    }

    // 3. Force Python unbuffered I/O for all child processes (yt-dlp is Python)
    SetEnvironmentVariableA("PYTHONUNBUFFERED", "1");

    // 4. Signal WPF we're alive
    sendMessage({ {"type","ready"} });

    // 5. Download tools if needed (WPF receives setup messages live)
    ensureResources();

    // 6. Command loop
    while (g_running)
    {
        std::string line = readLine();
        if (line.empty()) break;

        try
        {
            json cmd = json::parse(line);
            std::string type = cmd.value("type", "");

            if (type == "download")
            {
                DownloadJob job;
                job.id = cmd.value("id", "");
                job.url = cmd.value("url", "");
                job.quality = cmd.value("quality", "1080");
                job.audioOnly = cmd.value("audioOnly", false);
                job.format = cmd.value("format", "mp4");
                job.outputDir = cmd.value("outputDir", "downloads");
                std::thread(runDownload, job).detach();
            }
            else if (type == "quit")
            {
                break;
            }
        }
        catch (...)
        {
            sendMessage({ {"type","error"},
                          {"message","Invalid JSON command"} });
        }
    }

    CloseHandle(g_pipe);
    return 0;
}