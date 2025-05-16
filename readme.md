# EAS File Upload PoC

This repository demonstrates a complete file upload and download system using a .NET minimal API server and a .NET client, with PostgreSQL large object storage for efficient handling of large files. It also provides endpoints for extracting file metadata using external tools.

---

## Solution Structure

- **EAS_FIleupload_Poc/** – Minimal API server for file upload, download, and metadata extraction.
- **EAS_FileUpload_Poc.Client/** – Console client for uploading and downloading files, with progress reporting.

---

## Features

### Server (Minimal API)
- **Upload files** (streamed, with progress and hashing)
- **Download files** (streamed from PostgreSQL large objects)
- **Multiple upload endpoints** (raw body, Swagger UI, multi-file)
- **File metadata extraction**
  - Video metadata via `ffprobe`
  - General metadata via `exiftool`
- **Hash calculation** (SHA256, SHA1, MD5)
- **Entity Framework Core** for file metadata
- **Swagger UI** for API exploration (in development mode)

### Client (Console App)
- **Upload a file** to the server with progress, speed, and ETA
- **Download the file** back, showing download progress
- **Helpers** for formatting byte sizes and monitoring stream progress

---

## Quick Start

### Prerequisites
- .NET 7 or newer
- PostgreSQL (with large object support enabled)
- `ffprobe` and `exiftool` available on the server (for metadata endpoints)

### Setup
1. **Configure PostgreSQL**
   - Set the connection string in `EAS_FIleupload_Poc/appsettings.json`.
2. **Ensure external tools**
   - Make sure `ffprobe` and `exiftool` are installed and accessible on the server.
3. **Run the server**
   - Start the API project. Swagger UI will be available in development mode.
4. **Configure and run the client**
   - Edit the file path and server URL in `EAS_FileUpload_Poc.Client/Program.cs` as needed.
   - Run the client to upload and download a file, with progress displayed in the console.

---

## API Overview

- `POST /upload` – Upload a file via raw body (query: `fileName`, `contentType`), this can handle larger files (multiple gigabytes)
- `POST /upload-swagger` – Upload via Swagger UI/form-data
- `POST /upload-multi-swagger` – Upload multiple files via Swagger UI/form-data
- `GET /files/{id}` – Download a file by GUID
- `GET /files/{id}/hashes` – Get SHA256/SHA1/MD5 hashes
- `GET /files/{id}/metadata/ffprobe` – Extract video metadata
- `GET /files/{id}/metadata/Exif` – Extract general metadata

---

## How It Works

1. **Upload:**
   - The client streams a file to the server. The server stores it as a PostgreSQL large object, computes hashes, and saves metadata.
2. **Download:**
   - The client requests the file by ID. The server streams it back from PostgreSQL.
3. **Metadata:**
   - The server can extract and return metadata using `ffprobe` or `exiftool`.

---

## Requirements

- .NET 7+
- PostgreSQL (with large object support)
- ffprobe and exiftool (for metadata endpoints)

---

## Notes
- For development, Swagger UI is enabled for easy API testing.
- The solution is designed for efficient handling of large files and extensible metadata extraction.
