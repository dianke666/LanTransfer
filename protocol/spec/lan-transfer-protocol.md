# LanTransfer 协议规范

当前 Protocol_Version：**1.0**。本文是 Android_App 与 Windows_Client 之间唯一的契约（需求 24.1）。两端各自实现本规范，并且都必须通过 `protocol/test-vectors/` 中的全部测试向量。

## 0 约定

- 每个消息类型、字段和枚举值都标注“引入版本”。本文所列内容全部在 1.0 中引入。以后增加的内容在“引入”列写新的版本号，并在 `CHANGELOG.md` 中登记。
- “I”（Initiator）指发起 TCP 连接的一方，“R”（Responder）指接受连接的一方。
- 字节以十六进制书写，例如 `4C 54`。`‖` 表示按顺序拼接。
- 文中的“必须”“不得”是强制要求；不满足时按对应小节规定的错误处理。

## 1 协议常量

| 常量 | 值 |
|---|---|
| `DEFAULT_PORT`（TCP） | 42580 |
| 备用监听端口 | 42590–42599，仍不可用时由系统分配（端口 0） |
| `DISCOVERY_PORT`（UDP） | 42581 |
| 组播组 | 239.255.42.58 |
| 公告周期 | 2 s，每次加 ±0.2 s 随机抖动 |
| 设备列表过期 / 刷新 | 10 s / 1 s |
| 连接预算（手动连接、点选配对、可信直连、续传） | 10 s，包含 TCP 建立、前导交换和握手 |
| 扫码配对每个候选地址的预算 | 5 s |
| 前导响应超时 | 10 s |
| 配对响应超时 / 拒绝后冷却期 | 60 s / 60 s |
| Pairing_Token 有效期 / 最大失败次数 / 长度 | 5 min / 5 次 / 16 字节 |
| 文字送达确认超时 | 10 s |
| File_Offer 响应超时 | 接收方 60 s；发送方保底 65 s |
| 心跳空闲发送间隔 / 判定无响应 | 5 s / 15 s |
| 临时文件最长保留 / 清理周期 | 24 h / 1 h |
| `MAX_TEXT`（文字 UTF‑8 字节上限） | 1,048,576 |
| 帧头长度 / `MAX_PAYLOAD` | 7 字节 / 2,097,152 字节（2 MiB） |
| record 密文长度 | 17–65,535 字节（明文 1–65,519 字节） |
| 文件块上限 | 65,536 字节 |
| 每个任务未确认数据上限 | 4 MiB（4,194,304 字节） |
| FILE_ACK 粒度 | 每写入 256 KiB（262,144 字节）或每 200 ms，文件结束时再发 1 次 |
| 单个 File_Offer 文件数 / 单个文件大小上限 | 1–1000 / 1 TiB（1,099,511,627,776 字节） |
| 公告长度上限 | 512 字节 |
| 二维码文本长度上限 / 最多 IP 数 | 512 字节 / 4 |

## 2 通用编码规则

### 2.1 基本类型

| 类型 | 编码 |
|---|---|
| `u8` / `u16` / `u32` / `u64` | 无符号整数，大端序，分别占 1 / 2 / 4 / 8 字节 |
| `i64` | 二进制补码，大端序，8 字节。时间戳一律是自 Unix 纪元起的 UTC 毫秒 |
| `Enum` | `u8`。每个取值单独标注引入版本；收到协商版本中未定义的取值视为 `MalformedPayload` |
| `Bytes[N]` | 恰好 N 个原始字节 |
| `Bytes` | 任意长度的原始字节，长度由外层字段界定 |
| `String` | 标准 UTF‑8：每个码点使用最短编码，U+0000 编码为单个 `00`，U+10000 及以上编码为 1 个 4 字节序列，不加 BOM。长度由外层字段界定。编码端保证输入不含 Unpaired_Surrogate；解码端严格校验（见 2.4），不合法时为 `MalformedPayload` |

TEXT 消息的正文字段类型是 `Bytes` 而不是 `String`：正文是否为合法 UTF‑8 由文字规则（第 13 节）处理，非法时回复 TEXT_CORRUPT，而不是按解码错误关闭连接。

### 2.2 Struct（TLV）

Struct 由 0 个或多个字段依次拼接而成，每个字段为：

```text
Field := tag:u8 ‖ len:u32 ‖ value[len]
```

1. tag 必须严格递增。同一 tag 出现两次或顺序倒退时为 `MalformedPayload`。
2. 已知 tag 的 value 必须符合该字段的类型。定长类型（`u8`、`u16`、`u32`、`u64`、`i64`、`Enum`、`Bytes[N]`）的 len 必须与类型长度完全一致。
3. 缺少必需字段时为 `MalformedPayload`。
4. 未知 tag 整体跳过，不报错（需求 22.7、23.9、23.10）。
5. 任何字段都不得越过所在缓冲区的边界；Struct 必须恰好填满它所在的缓冲区。
6. 编码端只输出协商版本中已定义的字段，按 tag 升序输出；可选字段缺省时不输出。

没有任何字段的 Struct 编码为 0 字节。

### 2.3 List\<Struct\>

```text
List := count:u32 ‖ count × ( len:u32 ‖ Struct[len] )
```

各元素必须恰好填满该字段的 value，否则为 `MalformedPayload`。

### 2.4 严格 UTF‑8

解码端按 Unicode 标准表 3‑7（Well-Formed UTF-8 Byte Sequences）校验。以下情况都不合法：截断的多字节序列、非最短形式编码、编码 U+D800–U+DFFF 的序列、解码值大于 U+10FFFF 的序列、没有前导字节的续字节、出现 `C0`、`C1`、`F5`–`FF` 字节。

### 2.5 规范编码

同一个消息模型只有一种编码结果。解码器保留可选字段“是否出现”这一信息，因此对当前版本编码器产生的字节先解码再编码，得到逐字节相同的结果（需求 22.9）。含未知 tag 的输入再编码时不输出这些未知字段。

## 3 Discovery_Announcement（UDP）

```text
Announcement := "LTDA"（4C 54 44 41） ‖ Struct
```

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | device_id | Bytes[16] | 是 | | 1.0 |
| 2 | display_name | String | 是 | 1–32 个码点 | 1.0 |
| 3 | device_type | Enum | 是 | 1 = Android，2 = Windows，3 = iOS | 1.0 |
| 4 | proto_major | u8 | 是 | | 1.0 |
| 5 | proto_minor | u8 | 是 | | 1.0 |
| 6 | port | u16 | 是 | 1–65535，Transfer_Service 实际监听的端口 | 1.0 |

- 公告只包含以上 6 项（需求 5.6）。最坏情况约 183 字节，不超过 512 字节（需求 23.1）。
- 发送：在每个活动接口上，每个周期向该接口的定向广播地址和组播组 239.255.42.58（端口 42581，TTL 1）各发送 1 份。新接口出现时立即发送一次。
- 接收端按设备 ID 去重。以下数据报直接丢弃，不提示用户，也不重置该设备的过期计时（需求 23.2）：长度超过 512 字节、magic 不符、Struct 不合法或缺少字段、名称不是合法 UTF‑8 或码点数不在 1–32 之间、设备类型不在 {1, 2, 3} 中、端口为 0。
- 来源地址不是与接收接口处于同一子网的私有 IPv4 地址、或设备 ID 为本机 ID 的公告同样丢弃（需求 2.9、5.5）。

## 4 QR_Payload

```text
QrText := "LT1:" ‖ Base32(QrStruct)
```

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | device_id | Bytes[16] | 是 | | 1.0 |
| 2 | display_name | String | 是 | 1–32 个码点 | 1.0 |
| 3 | ipv4_list | Bytes | 是 | 长度为 4·n，n = 1–4；每 4 字节一个 IPv4 地址（大端），按优先级排列，互不相同 | 1.0 |
| 4 | port | u16 | 是 | 1–65535 | 1.0 |
| 5 | fingerprint | Bytes[32] | 是 | SHA‑256(显示方身份公钥) | 1.0 |
| 6 | token | Bytes[16] | 是 | Pairing_Token | 1.0 |

- Base32 使用 RFC 4648 字母表（`A–Z2–7`），大写，不加填充 `=`。`A–Z`、`2–7`、`:` 都属于 QR 码字母数字模式的字符集。
- 最坏长度约 388 字节，不超过 512 字节（需求 23.5）。
- 解析必须整体成功或整体失败，失败时不输出任何部分结果（需求 23.6）。以下任一情况都是解析错误：
  - 文本长度超过 512 字节，或不以 `LT1:` 开头；
  - Base32 部分含 `A–Z2–7` 以外的字符（包括小写字母和 `=`）；
  - Base32 部分的长度对 8 取模不属于 {0, 2, 4, 5, 7}；
  - 末尾多余的比特不为 0（保证规范编码）；
  - Struct 不合法、缺少字段，或字段不满足上表约束。
- 未知 tag 跳过（需求 23.9、23.10）。

## 5 连接前导与版本协商

```text
Preamble := "LANT"（4C 41 4E 54） ‖ major:u8 ‖ minor:u8        ; 固定 6 字节
```

- TCP 连接建立后，双方立即发送自己的前导，并读取对方的前导。前导只出现一次，不加密。
- magic 不符：直接关闭连接（对方不是本协议）。
- 10 s 内未收到对方的完整前导：发起方按“对方无版本响应”失败并关闭。
- 主版本不同：双方都提示版本不兼容（显示两边的“主版本号.次版本号”），然后关闭连接，不修改信任列表（需求 24.5）。
- 主版本相同：协商版本 = `(major, min(minor_I, minor_R))`。此后双方的编码器只输出协商版本中已定义的消息类型、字段和枚举值（需求 24.6）。
- 前导字节被写入 Noise 的 prologue（第 6 节），中间人篡改版本号会导致握手失败（防降级）。

## 6 握手：Noise_XX_25519_AESGCM_SHA256

按 Noise Protocol Framework（revision 34）实现，协议名为 `Noise_XX_25519_AESGCM_SHA256`：

- DH：X25519。Device_Identity 的身份密钥对就是 Noise 的静态密钥对 `s`。公钥指纹 = SHA‑256(32 字节公钥)。
- 加密：AES‑256‑GCM，nonce = `00 00 00 00 ‖ n:u64`（大端）。
- 哈希：SHA‑256；HKDF 使用 HMAC‑SHA256。
- Prologue：`"LanTransfer-Noise-v1"`（20 字节 ASCII） ‖ Preamble_I（6 字节） ‖ Preamble_R（6 字节）。

握手消息在 TCP 上的封装：

```text
HandshakeMessage := len:u16 ‖ noise_message[len]
```

| 消息 | 方向 | Noise 模式 | 载荷 |
|---|---|---|---|
| msg1 | I → R | `e` | 必须为空 |
| msg2 | R → I | `e, ee, s, es` | R 的 HandshakeIdentity，不含 intent |
| msg3 | I → R | `s, se` | I 的 HandshakeIdentity，必须含 intent |

HandshakeIdentity（Struct）：

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | device_id | Bytes[16] | 是 | | 1.0 |
| 2 | display_name | String | 是 | 接收方按显示名称规则（1–32 个码点、不含控制字符）检查 | 1.0 |
| 3 | device_type | Enum | 是 | 1 = Android，2 = Windows，3 = iOS | 1.0 |
| 4 | intent | Enum | msg3 中必需，msg2 中不得出现 | 1 = SESSION，2 = PAIR，3 = QR_PAIR | 1.0 |

- 持有私钥的证明：msg2 的载荷由包含 `es` 的密钥加密，msg3 的载荷由包含 `se` 的密钥加密；对方能正确加密这些载荷，就证明它持有所出示公钥对应的私钥（需求 10.2）。
- 任何一步失败（解密失败、载荷不合法、msg1 载荷非空、intent 缺失或出现位置不对）：关闭连接，不建立 Session，并提示安全警告（需求 10.6）。
- 发起方收到 msg2 后、发送 msg3 之前完成以下检查，不通过时直接关闭，不发送 msg3：
  - 对方设备 ID 等于本机设备 ID（自连接，需求 6.9）；
  - 对方是 Trusted_Device，但公钥与保存的不一致（需求 9.4）；
  - 扫码配对时，SHA‑256(对方公钥) 与 QR_Payload 中的 fingerprint 不一致（需求 8.10、10.7）。
- intent 的选择：对方是 Trusted_Device 且公钥一致 → SESSION；扫码配对 → QR_PAIR；其余 → PAIR。
- 握手完成后调用 `Split()` 得到两个 CipherState：第一个用于 I → R，第二个用于 R → I。

## 7 安全记录层

```text
Record := len:u16 ‖ ciphertext[len]          ; len = 17–65535
ciphertext := AES‑256‑GCM(k, nonce = 00 00 00 00 ‖ n:u64, ad = 空, plaintext = 1–65519 字节)
```

- 每个方向的计数器 n 从 0 开始，每条 record 加 1。n 的最大可用值为 2⁶⁴ − 2；下一条将使用 2⁶⁴ − 1 时视为 `IntegrityError`。
- record 的明文是 Frame 字节流的一个片段：一个 Frame 可以拆到多条 record 中，多个 Frame 也可以放进同一条 record。
- 以下情况统一视为 `IntegrityError`：解密失败、`len < 17`、计数器将溢出。被重放、重排或注入的 record 会因为 nonce 不匹配而解密失败；来自其他 Session 的 record 会因为密钥不同而解密失败。
- 出现 `IntegrityError` 时：丢弃该 record 及之后的全部数据，不输出之后的任何明文，终止 Session，并把与该对端进行中的任务标为“已中断”，原因为“数据完整性校验失败”（需求 10.5）。

## 8 Frame

```text
Frame := magic:u16 = 4C 54（"LT"） ‖ type:u8 ‖ length:u32 ‖ payload[length]      ; 帧头 7 字节
payload := 该类型的 Struct（第 9 节）
```

FrameDecoder 面对的是记录层输出的连续明文字节流：

- 收齐 7 字节帧头后立即检查，不等待载荷：
  - magic 不是 `4C 54` → `BadMagic`；
  - type 不是协商版本中定义的类型 → `UnknownType`；
  - `length > MAX_PAYLOAD` → `PayloadTooLarge`。
- 收齐载荷后按该类型的 Struct 规则解析，失败时为 `MalformedPayload`。
- 出错前已完整到达的消息先按顺序交付，然后报告错误；之后不再解析该连接的任何字节。
- 解码器缓存的字节数在任何时刻都不超过 `7 + MAX_PAYLOAD`。连接关闭时丢弃未完整到达的残留字节，不输出任何消息。
- 编码端：载荷超过 `MAX_PAYLOAD` 时不输出任何字节，返回 `PayloadTooLarge`（需求 22.11、22.12）。

解码错误类型（测试向量中使用括号内的名称）：

| 错误 | 含义 |
|---|---|
| `BadMagic`（`BAD_MAGIC`） | 帧头 magic 不符 |
| `UnknownType`（`UNKNOWN_TYPE`） | type 不在协商版本中 |
| `PayloadTooLarge`（`PAYLOAD_TOO_LARGE`） | length 超过 `MAX_PAYLOAD` |
| `MalformedPayload`（`MALFORMED_PAYLOAD`） | 载荷不符合该类型的 Struct 规则 |

## 9 Protocol_Message 类型

下表中 transfer_id、message_id 都是 `Bytes[16]`。全部类型、字段和枚举值都在 1.0 中引入。

| type | 名称 | 方向 |
|---|---|---|
| `01` | SESSION_RESULT | R → I |
| `02` | SESSION_CLOSE | 双向 |
| `03` | HEARTBEAT | 双向 |
| `10` | PAIR_REQUEST | I → R |
| `11` | PAIR_NONCE | R → I |
| `12` | PAIR_REVEAL | I → R |
| `13` | PAIR_DECISION | R → I |
| `14` | PAIR_CANCEL | I → R |
| `15` | PAIR_COMPLETE | I → R |
| `16` | QR_PAIR_REQUEST | 扫描方（I）→ 显示方（R） |
| `17` | QR_PAIR_RESULT | 显示方（R）→ 扫描方（I） |
| `20` | TEXT | 双向 |
| `21` | TEXT_ACK | 双向 |
| `22` | TEXT_CORRUPT | 双向 |
| `30` | FILE_OFFER | 发送方 → 接收方 |
| `31` | FILE_OFFER_REPLY | 接收方 → 发送方 |
| `32` | FILE_CHUNK | 发送方 → 接收方 |
| `33` | FILE_END | 发送方 → 接收方 |
| `34` | FILE_ACK | 接收方 → 发送方 |
| `35` | FILE_RESULT | 接收方 → 发送方 |
| `36` | FILE_READ_FAILED | 发送方 → 接收方 |
| `37` | TRANSFER_CANCEL | 双向 |
| `38` | RESUME_REQUEST | 发送方 → 接收方 |
| `39` | RESUME_STATE | 接收方 → 发送方 |
| `3A` | RESUME_PLAN | 发送方 → 接收方 |

### 9.1 Session 与心跳

**SESSION_RESULT（`01`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | result | Enum：0 ACCEPTED、1 NOT_TRUSTED、2 KEY_MISMATCH、3 DUPLICATE | 是 | 1.0 |

**SESSION_CLOSE（`02`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | reason | Enum：0 NORMAL、1 UNPAIRED、2 APP_EXIT、3 BACKGROUND、4 DUPLICATE | 是 | 1.0 |

**HEARTBEAT（`03`）**：没有字段，载荷为 0 字节。

### 9.2 点选配对

**PAIR_REQUEST（`10`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | commitment | Bytes[32] | 是 | 1.0 |

**PAIR_NONCE（`11`）**、**PAIR_REVEAL（`12`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | nonce | Bytes[32] | 是 | 1.0 |

**PAIR_DECISION（`13`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | decision | Enum：0 ACCEPTED、1 REJECTED、2 BUSY、3 TIMEOUT | 是 | 1.0 |

**PAIR_CANCEL（`14`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | reason | Enum：0 USER、1 TIMEOUT | 是 | 1.0 |

**PAIR_COMPLETE（`15`）**：没有字段。

### 9.3 扫码配对

**QR_PAIR_REQUEST（`16`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | token | Bytes[16] | 是 | 1.0 |

**QR_PAIR_RESULT（`17`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | result | Enum：0 OK、1 TOKEN_INVALID | 是 | 1.0 |

### 9.4 文字

**TEXT（`20`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | message_id | Bytes[16] | 是 | 1.0 |
| 2 | sent_at | i64（发送方 UTC 毫秒） | 是 | 1.0 |
| 3 | text | Bytes（UTF‑8，见第 13 节） | 是 | 1.0 |

**TEXT_ACK（`21`）**、**TEXT_CORRUPT（`22`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | message_id | Bytes[16] | 是 | 1.0 |

### 9.5 文件

**FILE_OFFER（`30`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | files | List\<FileEntry\> | 是 | 1.0 |

FileEntry（Struct）：

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | name | String | 是 | 文件名，不含路径；接收方按文件名规则处理后再使用 | 1.0 |
| 2 | size | u64 | 是 | ≤ 1 TiB | 1.0 |
| 3 | mime | String | 是 | ≤ 255 字节，可打印 ASCII（20–7E） | 1.0 |
| 4 | mtime | i64 | 否 | 源文件修改时间（UTC 毫秒），未知时不输出 | 1.0 |

接收方先做语义校验：文件数在 1–1000 之间、每个文件 size ≤ 1 TiB、mime 满足约束。不通过时回复 FILE_OFFER_REPLY(INVALID_OFFER)，Session 保持不断开。这些是语义错误，不属于 `MalformedPayload`。

**FILE_OFFER_REPLY（`31`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | decision | Enum：0 ACCEPT、1 REJECT、2 TIMEOUT、3 NO_SPACE、4 CANNOT_SAVE、5 INVALID_OFFER | 是 | 1.0 |

**FILE_CHUNK（`32`）**

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | | 1.0 |
| 2 | file_index | u32 | 是 | File_Offer 中的下标，从 0 开始 | 1.0 |
| 3 | offset | u64 | 是 | 本块在文件中的起始位置 | 1.0 |
| 4 | data | Bytes | 是 | 1–65,536 字节；超过上限为 `MalformedPayload` | 1.0 |

**FILE_END（`33`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | file_index | u32 | 是 | 1.0 |
| 3 | sha256 | Bytes[32]（整个文件内容的 SHA‑256） | 是 | 1.0 |

**FILE_ACK（`34`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | file_index | u32 | 是 | 1.0 |
| 3 | persisted | u64（该文件已写入接收方存储的字节数） | 是 | 1.0 |

**FILE_RESULT（`35`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | file_index | u32 | 是 | 1.0 |
| 3 | status | Enum：0 COMPLETED、1 CHECKSUM_FAILED、2 WRITE_FAILED | 是 | 1.0 |
| 4 | detail | String（失败说明） | 否 | 1.0 |

**FILE_READ_FAILED（`36`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | file_index | u32 | 是 | 1.0 |

**TRANSFER_CANCEL（`37`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | reason | Enum：0 USER、1 UNKNOWN_TRANSFER、2 UNPAIRED | 是 | 1.0 |

**RESUME_REQUEST（`38`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |

**RESUME_STATE（`39`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | files | List\<ResumeFileState\> | 是 | 1.0 |

ResumeFileState（Struct）：

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | file_index | u32 | 是 | 1.0 |
| 2 | state | Enum：0 NONE、1 PARTIAL、2 COMPLETED、3 TERMINAL | 是 | 1.0 |
| 3 | persisted | u64（临时文件的实际长度；NONE 时为 0） | 是 | 1.0 |

**RESUME_PLAN（`3A`）**

| tag | 名称 | 类型 | 必需 | 引入 |
|---|---|---|---|---|
| 1 | transfer_id | Bytes[16] | 是 | 1.0 |
| 2 | files | List\<ResumeFilePlan\> | 是 | 1.0 |

ResumeFilePlan（Struct）：

| tag | 名称 | 类型 | 必需 | 约束 | 引入 |
|---|---|---|---|---|---|
| 1 | file_index | u32 | 是 | | 1.0 |
| 2 | action | Enum：0 CONTINUE、1 RESTART、2 SKIP | 是 | | 1.0 |
| 3 | start | u64 | 是 | CONTINUE 时等于接收方报告的 persisted；RESTART 与 SKIP 时为 0 | 1.0 |
| 4 | size | u64 | 否 | 只在 RESTART 时出现，为源文件的新大小 | 1.0 |
| 5 | mtime | i64 | 否 | 只在 RESTART 时出现，为源文件的新修改时间 | 1.0 |

## 10 配对验证码

```text
nI, nR      := 各 32 字节，由 CSPRNG 生成，每次配对重新生成
commitment  := SHA‑256("LanTransfer-Commit-v1" ‖ pkI ‖ pkR ‖ nI)
SAS         := u64_be(SHA‑256("LanTransfer-SAS-v1" ‖ pkI ‖ pkR ‖ nI ‖ nR)[0..8]) mod 1,000,000，十进制，左侧补零到 6 位
```

- pkI、pkR 分别是发起方、接收方的 32 字节身份公钥（握手中交换的静态公钥）。两个前缀都是 ASCII 字符串，不含结尾 NUL。
- 消息顺序：I 发送 PAIR_REQUEST(commitment) → R 发送 PAIR_NONCE(nR) → I 发送 PAIR_REVEAL(nI) → R 校验承诺。双方都按“发起方在前”的顺序拼接。
- 揭示的 nI 与承诺不符时，按“配对失败，请重试”处理并关闭连接。
- R 在发送 PAIR_NONCE 之前判定是否自动拒绝（冷却期、正忙）；自动拒绝时直接回复 PAIR_DECISION(REJECTED 或 BUSY)。
- R 用户接受后先暂存信任，回复 PAIR_DECISION(ACCEPTED)；I 收到后保存信任并发送 PAIR_COMPLETE；R 收到 PAIR_COMPLETE 才提交信任。当前连接随即成为 Session。
- 超时：I 从发送 PAIR_REQUEST 起计时 60 s，到时发送 PAIR_CANCEL(TIMEOUT)；R 另有一个从收到 PAIR_REQUEST 起计时的 60 s 保底计时器。

## 11 扫码配对

- 扫描方按 QR_Payload 中 ipv4_list 的顺序尝试各个地址（过滤掉不在本机子网内的地址），每个地址最长 5 s，在第一个成功建立的连接处停止。
- 握手 intent = QR_PAIR。msg2 中显示方公钥的指纹必须与 QR_Payload 中的 fingerprint 一致，否则不发送 msg3，也不发送令牌。
- 握手完成后扫描方发送 QR_PAIR_REQUEST(token)。显示方校验令牌：5 分钟内生成、未被使用、是当前最新的令牌、失败次数少于 5 次。
  - 有效：显示方暂存信任并回复 QR_PAIR_RESULT(OK)；扫描方保存信任并发送 PAIR_COMPLETE；显示方收到后提交信任。
  - 无效：回复 QR_PAIR_RESULT(TOKEN_INVALID)，失败次数加 1。

## 12 Session 规则

- **待定阶段**：握手完成后连接处于 Pending 状态，直到 R 发送 SESSION_RESULT(ACCEPTED)，或点选配对、扫码配对完成。这一阶段只允许 SESSION_RESULT、SESSION_CLOSE、HEARTBEAT、PAIR_* 和 QR_PAIR_* 消息。收到其他消息（TEXT、FILE_* 等）时，回复 SESSION_RESULT(NOT_TRUSTED) 并关闭连接，不显示、不保存、不创建任何文件（需求 10.4）。
- **intent = SESSION**：R 检查 I 的设备 ID 与公钥，回复 SESSION_RESULT：
  - ACCEPTED：I 是 R 的 Trusted_Device 且公钥一致；
  - NOT_TRUSTED：I 不是 R 的 Trusted_Device；
  - KEY_MISMATCH：I 的设备 ID 是 Trusted_Device，但公钥不一致；
  - DUPLICATE：与 I 之间已有保留的 Session（见下条）。
  除 ACCEPTED 外，R 发送后关闭连接。
- **每个对端只保留 1 个 Session**：以对端设备 ID 为键。双方同时发起连接时，保留发起方设备 ID（按字节无符号比较）较小的那个连接；另一个连接回复 SESSION_RESULT(DUPLICATE) 或发送 SESSION_CLOSE(DUPLICATE) 后关闭。应用消息只在保留的 Session 上发送。
- **心跳**：连续 5 s 没有发送任何 Frame 时，发送 1 次 HEARTBEAT。连续 15 s 没有收到任何 Frame 时，判定 Session 断开，原因为“对方无响应”（需求 18.5、18.6）。
- **断开检测**：读到 EOF 或收到 RST，判定为“连接被对方关闭”；Session 所在的本地接口消失，判定为“本机网络已断开”。两者都在 2 s 内生效（需求 18.8）。
- **解码错误**：FrameDecoder 返回错误时，关闭连接，之后不再解析该连接的任何字节，并把该连接上进行中的任务标为“已中断”，原因为“协议数据错误”（需求 22.6）。
- **关闭**：主动关闭 Session 时先发送 SESSION_CLOSE，再关闭 TCP 连接。

## 13 文字规则

- 发送方保证 text 是合法 UTF‑8、不含 Unpaired_Surrogate 的编码结果，且长度不超过 `MAX_TEXT`（1,048,576 字节）。
- message_id 为 16 字节随机数；sent_at 为发送方的 UTC 毫秒时间。重试使用相同的 message_id 和相同的文本。
- 接收方处理顺序：
  1. 对 text 做严格 UTF‑8 解码（2.4）；失败时回复 TEXT_CORRUPT(message_id)，不保存、不显示，Session 不中断（需求 12.7）；
  2. 按 (发送方设备 ID, message_id) 去重；重复消息只回复 TEXT_ACK，不再显示（需求 11.8）；
  3. 写入传输记录；
  4. 写入成功后回复 TEXT_ACK(message_id)。写入失败时不回复 TEXT_ACK，但仍显示该消息（需求 20.8）。
- 发送方 10 s 内未收到 TEXT_ACK 时标为“发送失败”；之后迟到的 TEXT_ACK 仍把该消息改为“已送达”。收到 TEXT_CORRUPT 时标为“发送失败”（需求 12.12）。

## 14 文件传输规则

- **Offer**：发送方发送 FILE_OFFER；接收方在 60 s 内回复 FILE_OFFER_REPLY，超时自动回复 TIMEOUT。发送方从发出 Offer 起 65 s 内未收到回复时，按超时结束。等待回复期间连接断开时，任务结束为“连接已断开”，不进入“已中断”。
- **顺序**：按 File_Offer 中的顺序逐个文件传输。同一文件的 FILE_CHUNK 的 offset 必须严格连续（第一块为 0 或续传计划中的 start）；出现跳跃时视为协议错误。
- **流控**：同一任务中，发送方保证“已发送字节数 − 已确认字节数 ≤ 4 MiB”。接收方每写入 256 KiB 或每 200 ms 回复一次 FILE_ACK，文件结束时也回复一次。两端显示的已传输字节数都以 FILE_ACK 的 persisted 为准。
- **0 字节文件**：不发送 FILE_CHUNK，直接发送 FILE_END，sha256 为空内容的 SHA‑256。
- **校验与提交**：接收方收到 FILE_END 后，检查已接收字节数等于 size 且 SHA‑256 一致：一致则提交为最终文件并回复 FILE_RESULT(COMPLETED)；否则删除临时文件并回复 FILE_RESULT(CHECKSUM_FAILED)。已接收字节数超过 size 时立即判定 CHECKSUM_FAILED。
- **读取失败**：源文件打开或读取失败时，发送方发送 FILE_READ_FAILED；接收方删除该文件的临时文件，然后继续下一个文件。
- **写入失败**：接收方写入失败时回复 FILE_RESULT(WRITE_FAILED, detail)，双方把任务标为“已中断”，接收方保留临时文件。
- **续传**：Session 重新建立后，发送方发送 RESUME_REQUEST；接收方回复 RESUME_STATE（persisted 取临时文件的实际长度，临时文件不存在时报告 NONE）；发送方回复 RESUME_PLAN：
  - 源文件的大小和修改时间与 Offer 时一致：CONTINUE，start = persisted；
  - 不一致：RESTART，start = 0，并附带新的 size 和 mtime；
  - 已完成、读取失败、校验失败的文件：SKIP。
  续传不需要接收方再次确认。双方从本地数据重新计算 [0, start) 部分的 SHA‑256，再继续增量计算。
- **取消**：TRANSFER_CANCEL 发出或收到后 1–2 s 内停止收发；接收方在 5 s 内删除未完成文件的临时文件，保留已完成的文件。对“已中断”的任务取消时，本机先记录待发送的取消通知，下次与该对端建立 Session 后 2 s 内发出。
- **对端不认识该任务**：收到 RESUME_REQUEST、TRANSFER_CANCEL 等引用未知 transfer_id 的消息时，回复 TRANSFER_CANCEL(transfer_id, UNKNOWN_TRANSFER)；但收到的消息本身就是 reason = UNKNOWN_TRANSFER 的 TRANSFER_CANCEL 时不再回复，避免往复。收到 UNKNOWN_TRANSFER 的一方把任务标为“对方已取消”。
