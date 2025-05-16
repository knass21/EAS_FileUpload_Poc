
# EAS File Upload PoC

This project demonstrates a file upload and download system using a .NET client and a .NET minimal API server, with PostgreSQL large object storage.

---

## Client: EAS_FileUpload_Poc.Client/Program.cs

**Purpose:**  
Uploads a file to the server, shows upload progress, then downloads the file back, showing download progress.

**Key Steps:**
1. **Prepare File and HTTP Client:**  
   - Set file path, server URL, and create an `HttpClient` with a long timeout.
2. **Upload File:**  
   - Open the file as a stream.
   - Wrap the stream in a `ProgressStream` to report upload progress.
   - POST the file to the server using `StreamContent`.
   - Display progress, speed, and ETA in the console.
   - On success, parse the response for the file ID.
3. **Download File:**  
   - Use the returned file ID to construct a download URL.
   - Download the file as a stream, writing to disk.
   - Show download progress and speed.
4. **Helpers:**  
   - `FormatBytes` for human-readable byte sizes.
   - `ProgressStream` class to monitor upload progress.

**Usage:**  
Edit the file path and server URL as needed, then run the client. It will upload and then download the file, showing progress in the console.

---

## Server: EAS_FIleupload_Poc/Program.cs

**Purpose:**  
Implements a minimal API for file upload, download, and metadata extraction, storing files as PostgreSQL large objects.

**Key Endpoints:**
- `POST /upload`  
  Upload a file via raw body (query params: `fileName`, `contentType`).
- `POST /upload-swagger`  
  Upload a file via Swagger UI/form-data.
- `POST /upload-multi-swagger`  
  Upload multiple files via Swagger UI/form-data.
- `GET /files/{id}`  
  Download a file by GUID.
- `GET /files/{id}/hashes`  
  Get SHA256/SHA1/MD5 hashes for a file.
- `GET /files/{id}/metadata/ffprobe`  
  Extract video metadata using ffprobe.
- `GET /files/{id}/metadata/Exif`  
  Extract metadata using exiftool.

**Key Components:**
- **FileDbContext:**  
  Entity Framework context for file metadata.
- **FileStorageService:**  
  Handles upload (with streaming and hashing), download (streaming from PostgreSQL large objects), and metadata retrieval.
- **HashingService:**  
  Computes SHA256, SHA1, and MD5 hashes.
- **FFProbeMetadataService & ExifMetadataService:**  
  Extracts metadata from files using external tools.
- **LargeObjectDbStream:**  
  Custom stream for reading PostgreSQL large objects.

**Setup:**
- Configure PostgreSQL connection string in `appsettings.json`.
- Ensure `ffprobe` and `exiftool` are available on the server.
- Run the server; Swagger UI is available in development mode.

---

## How it works

1. **Upload:**  
   The client streams the file to the server. The server stores the file as a PostgreSQL large object, computes its hash, and saves metadata.
2. **Download:**  
   The client requests the file by ID. The server streams the file from PostgreSQL back to the client.
3. **Metadata:**  
   The server can extract and return metadata using ffprobe or exiftool.

---

## Requirements

- .NET 7+
- PostgreSQL (with large object support)
- ffprobe and exiftool (for metadata endpoints)

---
