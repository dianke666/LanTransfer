# 协议变更记录

每个 Protocol_Version 一节。新增消息类型、字段或枚举值时，在规范中的“引入”列写明版本号，并在这里登记。修改协议或测试向量时，两端测试必须同时通过。

## 1.0

首个版本，引入以下全部内容：

- 通用编码：大端整数、严格 UTF‑8 String、TLV Struct（tag 严格递增、未知 tag 跳过）、List\<Struct\>、规范编码。
- Discovery_Announcement（`LTDA`，tag 1–6；`device_type` 预留 `3 = iOS`）。
- QR_Payload（`LT1:` + RFC 4648 Base32，tag 1–6）。
- 连接前导 `LANT` + major + minor 与版本协商。
- 握手 Noise_XX_25519_AESGCM_SHA256，prologue `LanTransfer-Noise-v1` ‖ Preamble_I ‖ Preamble_R，HandshakeIdentity（tag 1–4）。
- 安全记录层（AES‑256‑GCM，`len:u16`）。
- Frame（magic `4C 54`，7 字节帧头，`MAX_PAYLOAD` = 2 MiB）。
- 消息类型 `01`–`03`、`10`–`17`、`20`–`22`、`30`–`3A` 及其全部字段与枚举值。
- 配对验证码（commitment、SAS）。
