# 协议测试向量

本目录是两端共用的唯一一份测试向量。Android 端（`core/*` 模块的测试资源）和 Windows 端（复制到各测试项目的输出目录 `test-vectors/`）直接读取这里的 JSON，不各自维护副本。

- 期望字节由 Kotlin 侧的向量生成工具手动生成（任务 6.1），CI 不重新生成。修改协议或向量时，两端测试必须同时通过，并更新 `protocol/spec/CHANGELOG.md`。
- 本目录在 `.gitattributes` 中设为 `-text`：文件按字节原样存取，请勿手工调整换行或行尾空白。
- 文件一律为 UTF‑8（无 BOM）JSON。

## 通用约定

每个向量文件的顶层结构：

```json
{
  "description": "这组向量覆盖的内容",
  "protocol_version": "1.0",
  "vectors": [ { "name": "向量名称，在同一文件内唯一", "...": "..." } ]
}
```

字段取值的写法：

| 协议类型 | JSON 写法 |
|---|---|
| `Bytes` / `Bytes[N]`、完整字节串 | 小写十六进制字符串，不带空格或 `0x`，例如 `"4c54"`；空字节串为 `""` |
| `String` | JSON 字符串。只含合法 Unicode 标量值；需要表示 Unpaired_Surrogate 的输入另用 `*_utf16` 字段（UTF‑16 码元的十六进制，大端） |
| `u8` / `u16` / `u32` | JSON 数字 |
| `u64` / `i64` | 十进制字符串，例如 `"1099511627776"`、`"-1"`，避免 JSON 数字的精度问题 |
| `Enum` | 规范中的取值名称，例如 `"ACCEPTED"`、`"ANDROID"` |
| `List<Struct>` | JSON 数组，元素为对象 |
| 可选字段 | 缺省时不写该键 |
| IPv4 地址 | 点分十进制字符串，例如 `"192.168.1.20"` |
| Protocol_Version | `"主版本号.次版本号"`，例如 `"1.0"` |

模型对象（`model`）的键名与规范中的字段名相同（snake_case）。

## manifest.json

列出当前已提交的向量文件，供两端的覆盖检查确认“每个文件都被读取”：

```json
{ "format_version": 1, "protocol_version": "1.0", "files": ["frames.json", "..."] }
```

## frames.json

每种 Protocol_Message 至少 1 个有效向量。

```json
{
  "name": "TEXT 含 NUL 与 CRLF",
  "type": "TEXT",
  "model": { "message_id": "00112233445566778899aabbccddeeff", "sent_at": "1735693805000", "text": "610d0a0062" },
  "bytes": "4c5420000000..."
}
```

- `type`：规范中的类型名称。
- `bytes`：完整 Frame（帧头 + 载荷）。
- 断言：`encode(model) == bytes`，`decode(bytes) == model`。
- 含未知 tag 的向量另有 `canonical_bytes`：此时断言 `decode(bytes) == model` 且 `encode(model) == canonical_bytes`（需求 22.7、22.9）。

## frames-invalid.json

每种解码错误至少 1 个样例。

```json
{
  "name": "帧头 magic 错误",
  "version": "1.0",
  "bytes": "4c5501000000...",
  "error": "BAD_MAGIC",
  "messages_before_error": 0
}
```

- `version`：解码器使用的协商版本。
- `error`：`BAD_MAGIC`、`UNKNOWN_TYPE`、`PAYLOAD_TOO_LARGE`、`MALFORMED_PAYLOAD` 之一。
- `messages_before_error`：错误之前应先交付的完整消息数（需求 22.4、22.5）。

## text-messages.json

需求 12.2–12.4 中每个字符类至少 1 个向量。

```json
{
  "name": "首尾空白与 U+3000",
  "class": ["edge-whitespace", "ideographic-space"],
  "text_utf8": "e38080...",
  "frame": "4c5420..."
}
```

- `class`：字符类标签数组，取值见两端测试辅助中的 `TextCharClass`（lf、cr、crlf、tab、consecutive-spaces、edge-whitespace、nbsp、ideographic-space、nul、c0-control、del、c1-control、zwj-sequence、skin-tone-modifier、variation-selector-16、zero-width、bom、combining-mark、supplementary-plane、private-use、unassigned、noncharacter）。
- `text_utf8`：文字的 UTF‑8 字节，以它为准。
- `frame`：以文件顶层 `message_id`、`sent_at` 编码的 TEXT Frame。
- 断言：严格 UTF‑8 解码后再编码得到 `text_utf8`；`encode(TEXT) == frame`；解码 `frame` 后得到的码点序列与 `text_utf8` 解码结果完全一致。

## utf8-invalid.json

```json
{ "name": "非最短形式 C0 80", "kind": "overlong", "text_utf8": "c080", "frame": "4c5420...", "expected": "TEXT_CORRUPT" }
```

- `kind`：truncated、overlong、surrogate、above-max、invalid-byte、unexpected-continuation、missing-continuation。
- 断言：`frame` 能作为 TEXT 正常解码（正文类型是 Bytes），但严格 UTF‑8 解码失败，接收方应回复 TEXT_CORRUPT（需求 12.7）。

## announcements.json

```json
{ "name": "32 个表情组成的名称", "model": { "device_id": "...", "display_name": "...", "device_type": "ANDROID", "proto_major": 1, "proto_minor": 0, "port": 42580 }, "bytes": "4c544441..." }
{ "name": "iOS 设备（预留 device_type=3）", "model": { "device_id": "...", "display_name": "iOS 设备", "device_type": "IOS", "proto_major": 1, "proto_minor": 0, "port": 42580 }, "bytes": "4c544441..." }
{ "name": "端口为 0", "bytes": "4c544441...", "error": "DROP" }
```

- 有效向量断言 `encode(model) == bytes`、`decode(bytes) == model`（`device_type` 支持 `ANDROID` / `WINDOWS` / 预留 `IOS`）；含 `error` 的向量断言解码结果为“丢弃”（需求 23.2）。

## qr-payloads.json

```json
{ "name": "4 个 IP、32 字符名称", "model": { "device_id": "...", "display_name": "...", "ipv4_list": ["192.168.1.20", "10.0.0.5"], "port": 42580, "fingerprint": "...", "token": "..." }, "text": "LT1:..." }
{ "name": "小写字母", "text": "LT1:abc...", "error": "PARSE_ERROR" }
```

- 有效向量断言 `encode(model) == text`、`decode(text) == model`；含 `error` 的向量断言整体解析失败、不输出部分结果（需求 23.6）。

## preamble.json

```json
{ "name": "1.0", "version": "1.0", "bytes": "4c414e540100" }
{ "name": "magic 错误", "bytes": "4c414e550100", "error": "INVALID" }
{ "name": "次版本不同", "local": "1.2", "remote": "1.0", "negotiated": "1.0" }
{ "name": "主版本不同", "local": "1.0", "remote": "2.0", "negotiated": "INCOMPATIBLE" }
```

- 编码向量断言 `encode(version) == bytes` 且 `decode(bytes) == version`；含 `error` 的向量断言解码失败（magic 不符或长度不是 6 字节）；协商向量断言 `negotiate(local, remote) == negotiated`。

## noise-xx.json

`vectors` 为空数组，内容在以下两个顶层字段中：

- `official`：Noise 官方测试向量中 `Noise_XX_25519_AESGCM_SHA256` 的用例子集，字段沿用官方格式（`init_prologue`、`init_static`、`init_ephemeral`、`resp_static`、`resp_ephemeral`、`messages[].payload` / `messages[].ciphertext`、`handshake_hash`）。
- `transcripts`：本协议用固定临时密钥生成的完整握手转录：前导、prologue、双方静态私钥与公钥、msg1–msg3（`identity` 为 HandshakeIdentity 模型，`payload` 为其编码，`message` 为 noise_message，不含 `len:u16`）、握手哈希，以及 Split 之后两个方向依次发送的 record（`plaintext` 为 Frame 字节，`record` 含 `len:u16`）。
- official 向量的 `messages` 依次为 msg1–msg3 与 2 条传输消息（先响应方、后发起方）；断言逐字节一致。

## verification-code.json

```json
{ "name": "...", "pk_i": "...", "pk_r": "...", "n_i": "...", "n_r": "...", "commitment": "...", "sas": "042137" }
```

## filename-sanitizer.json

```json
{
  "name": "合法名称原样保留",
  "input": "report.pdf",
  "now": "2025-01-01T09:30:05",
  "android": "report.pdf",
  "windows": "report.pdf"
}
```

- `input`：未经处理的文件名；含 Unpaired_Surrogate 时改用 `input_utf16`。
- `now`：回退名使用的本地时间（`yyyy-MM-ddTHH:mm:ss`）。
- `android` / `windows`：两个平台各自的期望输出。
- 可选 `steps`：列出该样例触发的处理步骤（S0–S2、L1–L4），供覆盖检查使用。
