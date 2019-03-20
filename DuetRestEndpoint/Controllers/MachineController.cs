﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading.Tasks;
using DuetAPIClient.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DuetRestEndpoint.Controllers
{
    /// <summary>
    /// MVC Controller for /machine requests
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class MachineController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger _logger;

        /// <summary>
        /// Create a new controller instance
        /// </summary>
        /// <param name="configuration">Launch configuration</param>
        /// <param name="logger">Logger instance</param>
        public MachineController(IConfiguration configuration, ILogger<MachineController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        #region General requests
        /// <summary>
        /// WS /machine
        /// Provide WebSocket for continuous model updates. This is primarily used to keep DWC2 up-to-date
        /// </summary>
        /// <returns>WebSocket, HTTP status code: (400) Bad request</returns>
        /// <seealso cref="WebSocketController.Process"/>
        [HttpGet]
        public async Task Get()
        {
            if (HttpContext.WebSockets.IsWebSocketRequest)
            {
                WebSocket webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
                await WebSocketController.Process(webSocket, _configuration.GetValue("SocketPath", "/tmp/duet.sock"),_logger);
            }
            else
            {
                HttpContext.Response.StatusCode = 400;
            }
        }

        /// <summary>
        /// GET /machine/status
        /// Retrieve the full object model as JSON.
        /// </summary>
        /// <returns>HTTP status code: (200) Machine object model as application/json (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            try
            {
                using (DuetAPIClient.CommandConnection connection = await BuildConnection())
                {
                    string json = await connection.GetSerializedMachineModel();
                    return Content(json, "application/json");
                }
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(Status)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(Status)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(Status)}] Failed to retrieve object model");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// POST /machine/code
        /// Execute a G/M/T-code and return the G-code response when done.
        /// </summary>
        /// <param name="code">G/M/T-code to execute</param>
        /// <returns>HTTP status code: (200) G-Code response as text/plain (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpPost]
        public async Task<IActionResult> DoCode([FromBody] string code)
        {
            try
            {
                using (DuetAPIClient.CommandConnection connection = await BuildConnection())
                {
                    string response = await connection.PerformSimpleCode(code);
                    return Content(response, "text/plain");
                }
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(DoCode)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(DoCode)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DoCode)}] Failed to execute code {code}");
                return StatusCode(500, e.Message);
            }
        }
        #endregion

        #region File requests
        /// <summary>
        /// GET /machine/file/{filename}
        /// Download the specified file.
        /// </summary>
        /// <param name="filename">File to download</param>
        /// <returns>HTTP status code: (200) File content (404) File not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpGet("file/{filename}")]
        public async Task<IActionResult> DownloadFile(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (System.IO.File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"{nameof(DownloadFile)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(filename);
                }

                FileStream stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return File(stream, "application/octet-stream");
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(DownloadFile)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(DownloadFile)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DownloadFile)}] Failed download file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/file/{filename}
        /// Upload a file from the HTTP body and create the subdirectories if necessary.
        /// </summary>
        /// <param name="filename">Destination of the file to upload</param>
        /// <returns>HTTP status code: (201) File created (500) Generic error occurred (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpPut("file/{filename}")]
        public async Task<IActionResult> UploadFile(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);

                // Create directory if necessary
                string directory = Path.GetDirectoryName(resolvedPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Write file
                using (FileStream stream = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write))
                {
                    await Request.Body.CopyToAsync(stream);
                }
                return Created(filename, null);
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(UploadFile)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(UploadFile)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(UploadFile)} Failed upload file {filename} ({Request.Body.Length} bytes, resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// GET /machine/fileinfo/{filename}
        /// Retrieve file info from the specified G-code file.
        /// </summary>
        /// <param name="filename">G-code file to analyze</param>
        /// <returns>HTTP status code: (200) File info as application/json (404) File not found (500) Generic error (502) Incompatible DCS (503) DCS is unavailable</returns>
        [HttpGet("fileinfo/{filename}")]
        public async Task<IActionResult> GetFileinfo(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);
                if (!System.IO.File.Exists(resolvedPath))
                {
                    _logger.LogWarning($"[{nameof(GetFileinfo)}] Could not find file {filename} (resolved to {resolvedPath})");
                    return NotFound(filename);
                }

                using (DuetAPIClient.CommandConnection connection = await BuildConnection())
                {
                    var info = await connection.GetFileInfo(resolvedPath);
                    string json = JsonConvert.SerializeObject(info, DuetAPI.JsonHelper.DefaultSettings);
                    return Content(json, "application/json");
                }
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(GetFileinfo)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(GetFileinfo)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(GetFileinfo)} Failed to retrieve file info for {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }
        #endregion
        
        #region Shared File and Directory requests
        /// <summary>
        /// DELETE /machine/file/{filename}
        /// Delete the given file or directory.
        /// </summary>
        /// <param name="filename">File or directory to delete</param>
        /// <returns>HTTP status code: (204) File or directory deleted (404) File not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpDelete("file/{filename}")]
        public async Task<IActionResult> DeleteFileOrDirectory(string filename)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(filename);

                if (Directory.Exists(resolvedPath))
                {
                    Directory.Delete(resolvedPath);
                    return NoContent();
                }

                if (System.IO.File.Exists(resolvedPath))
                {
                    System.IO.File.Delete(resolvedPath);
                    return NoContent();
                }

                _logger.LogWarning($"[{nameof(DeleteFileOrDirectory)} Could not find file {filename} (resolved to {resolvedPath})");
                return NotFound(filename);
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(DeleteFileOrDirectory)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(DeleteFileOrDirectory)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(DeleteFileOrDirectory)} Failed to delete file {filename} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// Move a file or directory from a to b
        /// </summary>
        /// <param name="from">Source path</param>
        /// <param name="to">Destination path</param>
        /// <param name="force">Delete existing file (optional, default false)</param>
        /// <returns>HTTP status code: (204) File or directory moved (404) File or directory not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpPost("file/move")]
        public async Task<IActionResult> MoveFileOrDirectory([FromForm] string from, [FromForm] string to, [FromForm] bool force = false)
        {
            string source = "n/a", destination = "n/a";
            try
            {
                source = await ResolvePath(from);
                destination = await ResolvePath(to);

                // Make sure the source path exists
                if (!Directory.Exists(source) && !System.IO.File.Exists(source))
                {
                    return NotFound(source);
                }
                
                // Delete destination file if applicable
                if (System.IO.File.Exists(to) && force)
                {
                    System.IO.File.Delete(to);
                }
                
                // Check for conflicts
                if ((Directory.Exists(source) && Directory.Exists(destination)) || (System.IO.File.Exists(source) && System.IO.File.Exists(destination)))
                {
                    return Conflict();
                }
                
                // Attempt to move the file or directory
                if (Directory.Exists(source))
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    System.IO.File.Move(source, destination);
                }

                return NoContent();
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(MoveFileOrDirectory)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(MoveFileOrDirectory)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(MoveFileOrDirectory)} Failed to move file {from} to {to} (resolved to {source} and {destination})");
                return StatusCode(500, e.Message);
            } 
        }
        #endregion
        
        #region Directory requests
        /// <summary>
        /// GET /machine/directory/{directory}
        /// Get a file list of the specified directory
        /// </summary>
        /// <param name="directory">Directory to query</param>
        /// <returns>HTTP status code: (200) File list as application/json (404) Directory not found (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpGet("directory/{directory}")]
        public async Task<IActionResult> GetFileList(string directory)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(directory);
                if (!Directory.Exists(directory))
                {
                    return NotFound(directory);
                }
                List<object> fileList = new List<object>();

                // List directories
                foreach (string dir in Directory.EnumerateDirectories(resolvedPath))
                {
                    DirectoryInfo info = new DirectoryInfo(dir);
                    fileList.Add(new { type = 'd', name = info.Name, date = info.LastWriteTime });
                }
                
                // List files
                foreach (string file in Directory.EnumerateFiles(resolvedPath))
                {
                    FileInfo info = new FileInfo(file);
                    fileList.Add(new { type = 'f', name = info.Name, size = info.Length, date = info.LastWriteTime });
                }
                
                string json = JsonConvert.SerializeObject(fileList, DuetAPI.JsonHelper.DefaultSettings);
                return Content(json, "application/octet-stream");
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(GetFileList)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(GetFileList)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(GetFileList)}] Failed to retrieve file list for {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            }
        }

        /// <summary>
        /// PUT /machine/directory/{directory}
        /// Create the given directory.
        /// </summary>
        /// <returns>HTTP status code: (204) Directory created (500) Generic error (502) Incompatible DCS version (503) DCS is unavailable</returns>
        [HttpPut("directory/{directory}")]
        public async Task<IActionResult> CreateDirectory(string directory)
        {
            string resolvedPath = "n/a";
            try
            {
                resolvedPath = await ResolvePath(directory);
                Directory.CreateDirectory(resolvedPath);
                return Created(directory, null);
            }
            catch (IncompatibleVersionException e)
            {
                _logger.LogError($"[{nameof(CreateDirectory)}] Incompatible DCS version");
                return StatusCode(502, e.Message);
            }
            catch (SocketException e)
            {
                _logger.LogError($"[{nameof(CreateDirectory)}] DCS is unavailable");
                return StatusCode(503, e.Message);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"[{nameof(CreateDirectory)}] Failed to create directory {directory} (resolved to {resolvedPath})");
                return StatusCode(500, e.Message);
            } 
        }
        #endregion

        private async Task<DuetAPIClient.CommandConnection> BuildConnection()
        {
            DuetAPIClient.CommandConnection connection = new DuetAPIClient.CommandConnection();
            await connection.Connect(_configuration.GetValue("SocketPath", "/tmp/duet.sock"));
            return connection;
        }

        private async Task<string> ResolvePath(string path)
        {
            using (DuetAPIClient.CommandConnection connection = await BuildConnection())
            {
                return await connection.ResolvePath(path);
            }
        }
    }
}
