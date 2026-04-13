# LLM Wrapper Gateway - Hướng dẫn chi tiết

## Mục đích dự án

Gateway trung gian cho phép client gọi API theo **format chuẩn OpenAI/Ollama**,
gateway sẽ tự động **transform** request/response sang format của API công ty (hoặc bất kỳ LLM provider nào).

Mỗi "wrapper" là một cấu hình mapping, lưu trong MySQL. Gateway **không lưu key** -
client tự giữ api-key của công ty, gateway chỉ đổi tên header rồi chuyển tiếp.

---

## Database - Bảng Wrappers

### SQL CREATE TABLE

```sql
CREATE TABLE `Wrappers` (
    `Id`               CHAR(36)      NOT NULL,
    `Name`             VARCHAR(255)  NOT NULL,
    `Provider`         VARCHAR(50)   NOT NULL DEFAULT 'local',
    `BaseUrl`          VARCHAR(1024) NOT NULL,
    `Session`          VARCHAR(512)  NULL,
    `RequestMapping`   TEXT          NULL,
    `ResponseMapping`  TEXT          NULL,
    `CreatedAt`        DATETIME(6)   NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (`Id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
```

### Giải thích từng cột

| Cột | Kiểu | Bắt buộc | Mô tả |
|-----|------|----------|--------|
| `Id` | CHAR(36) | PK | Guid định danh wrapper, dùng trong URL `/w/{Id}/...` |
| `Name` | VARCHAR(255) | Yes | Tên hiển thị, ví dụ `"Company ABC LLM"` |
| `Provider` | VARCHAR(50) | Yes | Loại provider: `"local"`, `"ollama"`, `"openai"` |
| `BaseUrl` | VARCHAR(1024) | Yes | URL gốc API đích, ví dụ `http://example.com` |
| `Session` | VARCHAR(512) | No | Session ID. Nếu có → URL = `{BaseUrl}/v1/{Session}?stream=...` |
| `RequestMapping` | TEXT | No | JSON template transform body client → body company (tự do, tùy công ty) |
| `ResponseMapping` | TEXT | No | JSON template transform response company → response client |
| `CreatedAt` | DATETIME(6) | Yes | Thời điểm tạo |

### Ghi chú quan trọng

- **Không có cột ApiKey / WrapperKey** - Gateway không lưu key bảo mật
- Client tự giữ api-key của công ty, gửi qua header `api-key`
- Gateway chỉ đổi tên header: `api-key` → `x-api-key` rồi forward
- `RequestMapping` là **free-form JSON template** - mỗi công ty cấu trúc body khác nhau, user tự định nghĩa

### Ví dụ data trong bảng

| Id | Name | Provider | BaseUrl | Session | RequestMapping |
|----|------|----------|---------|---------|----------------|
| a1b2... | Company ABC | local | http://abc.com | sess-123 | `{"input":{"text":"{messages[0].content}"}}` |
| c3d4... | Company XYZ | local | http://xyz.com | sid-456 | `{"payload":{"prompt":"{messages[0].content}"},"meta":{"ver":"v2"}}` |
| e5f6... | My Ollama | ollama | http://localhost:11434 | *(null)* | *(null - passthrough)* |

---

## Cấu trúc thư mục

```
LLMWrapperGateway/
├── Program.cs                        # Entry point - đăng ký DI, định nghĩa tất cả API routes
├── appsettings.json                  # Connection string MySQL, cấu hình logging
│
├── Models/
│   └── WrapperConfig.cs              # Entity database + DTO tạo wrapper
│
├── Data/
│   └── WrapperDbContext.cs           # EF Core DbContext, cấu hình bảng Wrappers
│
├── Services/
│   └── WrapperManager.cs            # Business logic CRUD wrapper
│
├── Helpers/
│   └── JsonMappingHelper.cs         # Engine replace placeholder trong JSON template
│
├── Middleware/
│   └── AuthMiddleware.cs            # Kiểm tra header "api-key", passthrough cho route /w/*
│
└── LLMWrapperGateway.csproj         # Package references
```

---

## Giải thích từng file

### 1. `Program.cs` - Trung tâm điều phối

**Tác dụng:** File chính của ứng dụng, làm 3 việc:

- **Đăng ký services** (Database, HttpClient, Swagger)
- **Định nghĩa Management API** (CRUD wrapper - không cần auth)
- **Định nghĩa Proxy route** `/w/{wrapperId}/{**path}` (cần header `api-key`)

**Luồng xử lý proxy (route `/w/{wrapperId}/{**path}`):**

```
Bước 1: Lấy api-key từ middleware, load WrapperConfig từ DB theo wrapperId
Bước 2: Đọc body request từ client
Bước 3: Parse "stream" từ body (true/false)
Bước 4: Build URL upstream
         - Có Session  → http://example.com/v1/{sessionId}?stream=false
         - Không Session → http://ollama:11434/v1/chat/completions
Bước 5: Transform body qua RequestMapping (thay placeholder)
Bước 6: Tạo HttpRequestMessage, đổi header api-key → x-api-key, gửi đi
Bước 7: Nhận response
         - Streaming → pipe trực tiếp về client (SSE)
         - Không streaming → transform qua ResponseMapping → trả JSON
```

---

### 2. `Models/WrapperConfig.cs` - Định nghĩa dữ liệu

**Tác dụng:** Chứa 2 class:

| Class | Mô tả |
|-------|--------|
| `WrapperConfig` | Entity map với bảng `Wrappers` trong MySQL |
| `CreateWrapperRequest` | DTO nhận data từ client khi tạo wrapper mới |

**Các trường trong WrapperConfig:**

| Trường | Ý nghĩa |
|--------|---------|
| `Id` | Guid - định danh wrapper, dùng trong URL `/w/{Id}/...` |
| `Name` | Tên hiển thị, ví dụ "Company LLM Production" |
| `Provider` | Loại provider: `"local"`, `"ollama"`, `"openai"` |
| `BaseUrl` | URL gốc của API đích, ví dụ `http://example.com` |
| `Session` | Session ID của company API. Nếu có → URL = `{BaseUrl}/v1/{Session}?stream=...` |
| `RequestMapping` | JSON template tự do - transform body client → body company API |
| `ResponseMapping` | JSON template tự do - transform response company → response client |

---

### 3. `Data/WrapperDbContext.cs` - Kết nối Database

**Tác dụng:** Cấu hình Entity Framework Core để làm việc với MySQL.

- Map class `WrapperConfig` → bảng `Wrappers`
- Định nghĩa kiểu cột, độ dài
- `RequestMapping` và `ResponseMapping` dùng kiểu `TEXT` (chứa JSON template dài)

---

### 4. `Services/WrapperManager.cs` - Business Logic

**Tác dụng:** Xử lý toàn bộ logic CRUD cho wrapper.

| Method | Mô tả |
|--------|--------|
| `CreateAsync()` | Tạo wrapper mới (chỉ lưu cấu hình mapping, không sinh key) |
| `ListAsync()` | Lấy tất cả wrapper, sắp xếp mới nhất trước |
| `GetByIdAsync()` | Tìm wrapper theo Guid |
| `DeleteAsync()` | Xóa wrapper |

---

### 5. `Helpers/JsonMappingHelper.cs` - Engine Transform JSON

**Tác dụng:** Thay thế placeholder `{...}` trong JSON template bằng giá trị thực từ body client.

**RequestMapping là free-form** - mỗi công ty có cấu trúc body khác nhau:

Công ty A:
```json
{"input": {"text": "{messages[0].content}"}, "config": {"model_name": "{model}"}}
```

Công ty B:
```json
{"payload": {"prompt": "{messages[0].content}"}, "meta": {"session": "{session}"}}
```

Công ty C:
```json
{"query": "{messages[0].content}"}
```

Gateway không quan tâm cấu trúc gì - chỉ tìm `{placeholder}` và replace.

**Placeholder cho RequestMapping:** (lấy từ body client gửi)

| Placeholder | Ví dụ giá trị |
|-------------|----------------|
| `{model}` | `"llama3"` |
| `{temperature}` | `0.7` |
| `{messages}` | `[{"role":"user",...}]` (cả array) |
| `{messages[0].content}` | `"Xin chào"` |
| `{messages[0].role}` | `"user"` |
| `{session}` | `"session-id-123"` (từ WrapperConfig.Session) |

#### ResponseMapping - Chuyển response company → format Ollama/OpenAI

Company API trả response dạng custom, ví dụ:
```json
{
  "result": {
    "output": "Xin chào! Tôi có thể giúp gì?",
    "tokens_used": 42
  },
  "status": "success"
}
```

ResponseMapping sẽ **chọn đúng field chứa text** rồi wrap thành format chuẩn OpenAI:
```json
{
  "id": "chatcmpl-wrapper",
  "object": "chat.completion",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "{result.output}"
      },
      "finish_reason": "stop"
    }
  ]
}
```

Kết quả client nhận được (chuẩn OpenAI, LangChain/LiteLLM đọc được):
```json
{
  "id": "chatcmpl-wrapper",
  "object": "chat.completion",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Xin chào! Tôi có thể giúp gì?"
      },
      "finish_reason": "stop"
    }
  ]
}
```

**Placeholder cho ResponseMapping:** (lấy từ response company trả về)

| Company response | Placeholder | Lấy được |
|------------------|-------------|----------|
| `{"result":{"output":"Hello"}}` | `{result.output}` | `Hello` |
| `{"data":{"text":"Hello"}}` | `{data.text}` | `Hello` |
| `{"answer":"Hello"}` | `{answer}` | `Hello` |
| `{"results":[{"content":"Hello"}]}` | `{results[0].content}` | `Hello` |

> **Nếu ResponseMapping = null** → trả nguyên response company (không transform).
> Khi bạn xem được response thật từ company, set ResponseMapping để chọn đúng field chứa text.

---

### 6. `Middleware/AuthMiddleware.cs` - Chuyển tiếp API Key

**Tác dụng:** Kiểm tra header `api-key` tồn tại cho route `/w/*`, lưu lại để proxy handler forward.

**Luồng:**

```
Request đến
  │
  ├─ Path bắt đầu bằng /w/ ?
  │   ├─ KHÔNG → bỏ qua, chạy tiếp (Management API không cần auth)
  │   └─ CÓ → kiểm tra header "api-key"
  │       ├─ Không có header → 401 Unauthorized
  │       └─ Có header → lưu vào context.Items["ClientApiKey"], chạy tiếp
  │
  ▼
  Proxy Handler → forward header: api-key → x-api-key cho company API
```

**Gateway không validate key** - chỉ chuyển tiếp. Company API tự validate.

---

### 7. `appsettings.json` - Cấu hình

**Tác dụng:** Chứa connection string MySQL.

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Port=3306;Database=llm_wrapper_gateway;User=root;Password=your_password;"
  }
}
```

Sửa `Server`, `Port`, `User`, `Password` cho phù hợp với MySQL của bạn.

---

## Luồng chạy tổng thể

```
                         LLM Wrapper Gateway
                    ┌─────────────────────────────┐
                    │                             │
 ┌──────────┐      │  ┌──────────────────────┐   │      ┌──────────────────┐
 │  Client   │      │  │   AuthMiddleware     │   │      │  Company API     │
 │ (Postman, │ ──── │  │  check "api-key"     │   │      │  http://example  │
 │  LangChain│ POST │  │  header tồn tại?     │   │      │  .com/v1/{sid}   │
 │  LiteLLM) │      │  └──────────┬───────────┘   │      │                  │
 └──────────┘      │             │               │      │  Header:         │
                    │  ┌──────────▼───────────┐   │      │  x-api-key: xxx  │
  Format chuẩn:     │  │   Proxy Handler      │   │ ──── │  (= api-key từ   │
  OpenAI/Ollama     │  │                      │   │ POST │   client)         │
                    │  │  1. Load wrapper DB  │   │      │                  │
  Header:           │  │  2. Read body        │   │      │  Body:           │
  api-key: abc123   │  │  3. Parse stream     │   │      │  (transformed    │
  (key công ty)     │  │  4. Build URL        │   │      │   theo mapping)  │
                    │  │  5. Transform body   │   │      └──────────────────┘
  Body:             │  │  6. Forward:         │   │               │
  {"model":"x",     │  │     api-key→x-api-key│   │               │
   "messages":[…],  │  │  7. Transform resp   │   │      ┌────────▼─────────┐
   "stream":false}  │  │                      │   │      │  Response        │
                    │  └──────────────────────┘   │      │  (company format)│
                    │                             │      └────────┬─────────┘
                    └─────────────────────────────┘               │
                                   │                     Transform ngược
                          ┌────────▼─────────┐          (ResponseMapping)
                          │  MySQL           │                    │
                          │  Table: Wrappers │           ┌────────▼─────────┐
                          │                  │           │  Response        │
                          │  - Id            │           │  (trả về client) │
                          │  - Name          │           └──────────────────┘
                          │  - BaseUrl       │
                          │  - Session       │
                          │  - RequestMapping│
                          │  - ResponseMapping│
                          │                  │
                          │  (KHÔNG lưu key) │
                          └──────────────────┘
```

**Mapping header:**
```
Client                Gateway               Company API
───────               ───────               ───────────
api-key: abc123  →    passthrough     →     x-api-key: abc123
                      (chỉ đổi tên)
```

---

## Ví dụ cụ thể end-to-end

### Bước 1: Tạo wrapper (cấu hình mapping cả request + response)

```http
POST /api/wrappers
Content-Type: application/json

{
  "name": "Company ABC LLM",
  "provider": "local",
  "baseUrl": "http://example.com",
  "session": "abc-session-123",
  "requestMapping": "{\n  \"input\": {\"text\": \"{messages[0].content}\"},\n  \"config\": {\"model_name\": \"{model}\"}\n}",
  "responseMapping": "{\n  \"id\": \"chatcmpl-wrapper\",\n  \"object\": \"chat.completion\",\n  \"choices\": [{\n    \"index\": 0,\n    \"message\": {\"role\": \"assistant\", \"content\": \"{result.output}\"},\n    \"finish_reason\": \"stop\"\n  }]\n}"
}
```

Response:
```json
{
  "id": "d4f5a6b7-...",
  "name": "Company ABC LLM",
  "baseUrl": "http://example.com",
  "session": "abc-session-123",
  "requestMapping": "...",
  "responseMapping": "..."
}
```

> Wrapper chỉ là cấu hình mapping, không chứa key.

### Bước 2: Client gọi wrapper (format chuẩn Ollama/OpenAI)

```http
POST /w/d4f5a6b7-.../v1/chat/completions
api-key: abc123-company-secret-key
Content-Type: application/json

{
  "model": "gpt-4",
  "messages": [{"role":"user","content":"Hello world"}],
  "stream": false
}
```

### Bước 3: Gateway transform request → forward tới company

```
URL:    POST http://example.com/v1/abc-session-123?stream=false
Header: x-api-key: abc123-company-secret-key      ← đổi tên từ api-key
Body:   {"input":{"text":"Hello world"},"config":{"model_name":"gpt-4"}}
                    ↑                                    ↑
             {messages[0].content}                    {model}
```

### Bước 4: Company API trả response (format custom)

```json
{
  "result": {
    "output": "Xin chào! Tôi có thể giúp gì cho bạn?",
    "tokens_used": 15
  },
  "status": "success"
}
```

### Bước 5: Gateway transform response → trả client (format chuẩn OpenAI)

ResponseMapping chọn `{result.output}` và wrap thành format OpenAI:

```json
{
  "id": "chatcmpl-wrapper",
  "object": "chat.completion",
  "choices": [
    {
      "index": 0,
      "message": {
        "role": "assistant",
        "content": "Xin chào! Tôi có thể giúp gì cho bạn?"
      },
      "finish_reason": "stop"
    }
  ]
}
```

> LangChain / LiteLLM / bất kỳ OpenAI client nào đều đọc được response này.

### Tóm tắt flow transform 2 chiều

```
CLIENT (OpenAI format)          GATEWAY              COMPANY API (custom format)
──────────────────────          ───────              ─────────────────────────────

Request:                                             Request:
  {"messages":[                  RequestMapping        {"input":{"text":"Hello"},
    {"content":"Hello"}   ──────────────────────►       "config":{"model_name":"gpt-4"}}
  ],"model":"gpt-4"}         {messages[0].content}
                                  {model}

Response:                                            Response:
  {"choices":[{                  ResponseMapping       {"result":
    "message":{              ◄──────────────────────     {"output":"Xin chào!"},
      "content":"Xin chào!"     {result.output}         "status":"success"}
    }}]}
```

---

## Cài đặt & Chạy

```bash
# 1. Sửa connection string trong appsettings.json

# 2. Restore packages
dotnet restore

# 3. Tạo migration
dotnet ef migrations add InitialCreate --project LLMWrapperGateway

# 4. Chạy app (auto-migrate khi startup)
dotnet run --project LLMWrapperGateway

# 5. Mở Swagger
# http://localhost:5000/swagger
```

## Packages sử dụng

| Package | Mục đích |
|---------|----------|
| `Pomelo.EntityFrameworkCore.MySql` | EF Core provider cho MySQL |
| `Microsoft.EntityFrameworkCore.Design` | Hỗ trợ lệnh `dotnet ef migrations` |
| `Microsoft.AspNetCore.OpenApi` | Extension `.WithOpenApi()` cho Minimal APIs |
| `Swashbuckle.AspNetCore` | Swagger UI |
| `Yarp.ReverseProxy` | Reverse proxy (dự phòng mở rộng) |
