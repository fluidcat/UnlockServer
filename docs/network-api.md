# 局域网控制接口（网络锁屏 / 解锁）

主程序可以作为局域网服务端，接受已授权客户端发来的锁屏与解锁请求。
网络是**独立触发源**：不要求蓝牙在场，也不依赖绑定设备的远近判定。

## 启用

1. 打开「网络」页，勾选「启用网络控制」。
2. 填写监听地址（`0.0.0.0` 表示所有网卡）与端口（默认 `2085`）。
3. 勾选「允许网络锁屏」／「允许网络解锁」（解锁默认关闭）。
4. 在「客户端密钥」区点「生成密钥」，立刻复制密钥串——**只显示这一次**，之后只保留指纹。
5. 若要用其它设备访问，确认 Windows 防火墙已放行该端口（程序会自动加一条「专用网络」入站规则）。

> 安全边界：网络解锁不校验蓝牙在场，签名客户端就是唯一的门。请只在可信局域网内开启，
> 不要映射到公网，也不要把密钥贴到聊天工具里传。

## 鉴权

### 1. 取挑战（一次性 nonce）

```
GET /v1/challenge?client=<clientId>
→ 200 {"nonce":"...","expiresIn":30}
```

`nonce` 单次有效、30 秒过期，由服务端签发，所以客户端不需要同步时钟。

### 2. 签名

```
signature = Base64( HMAC-SHA256( key, canonical ) )
canonical = clientId.ToLower() + "|" + METHOD.ToUpper() + "|" + path + "|" + nonce + "|" + sha256hex(body)
```

请求头：

| 头 | 值 |
|---|---|
| `X-Client-Id` | 生成密钥时的客户端 ID |
| `X-Nonce` | 上一步拿到的 nonce |
| `X-Signature` | 上式结果 |

校验失败统一返回 `401 {"error":"..."}`；同一 nonce 不可重复使用；1 分钟内失败 5 次会锁 5 分钟。

## 接口

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/v1/challenge?client=` | 取 nonce（唯一不要求签名的接口） |
| GET | `/v1/state` | 当前是否锁定、待执行命令数、控制台会话号 |
| POST | `/v1/lock` | 锁屏，返回 `202 {"requestId":"..."}` |
| POST | `/v1/unlock` | 解锁，返回 `202 {"requestId":"..."}` |
| GET | `/v1/requests/<id>` | 回查执行结果 |

`status` 取值：`pending`（排队中）、`done`（已执行）、`rejected`（被拒绝）。
`reason` 常见值：

| reason | 含义 |
|---|---|
| `already_in_state` | 已经是目标状态，未重复执行 |
| `provider_not_ready` | 本机解锁组件未安装或没有保存凭据，无法解锁 |
| `not_console` | 没有可操作的控制台会话 |
| `busy` | 解锁测试进行中，或队列已满 |
| `unverified` | 锁屏已下发，但 0.8 秒内没观测到会话状态翻转（锁屏 API 本身无返回值） |
| `authentication_failed_or_timeout` | 凭据提供程序超时或认证失败 |

## 客户端示例

```python
import base64, hashlib, hmac, json, urllib.request

HOST = "http://192.168.1.20:2085"
CLIENT_ID = "cxxxxxxxxxxxx"
KEY = base64.b64decode("...")          # 生成时拿到的密钥串


def sha256hex(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def sign(method, path, nonce, body=""):
    canonical = "|".join([CLIENT_ID.lower(), method.upper(), path, nonce, sha256hex(body)])
    return base64.b64encode(hmac.new(KEY, canonical.encode("utf-8"), hashlib.sha256).digest()).decode()


def call(method, path, body=""):
    nonce = json.load(urllib.request.urlopen(HOST + "/v1/challenge?client=" + CLIENT_ID))["nonce"]

    data = body.encode("utf-8") if body else None
    request = urllib.request.Request(HOST + path, data=data, method=method)
    request.add_header("X-Client-Id", CLIENT_ID)
    request.add_header("X-Nonce", nonce)
    request.add_header("X-Signature", sign(method, path, nonce, body))
    with urllib.request.urlopen(request) as response:
        return response.status, json.loads(response.read())


print(call("POST", "/v1/lock"))
print(call("GET", "/v1/state"))
```

## 行为约定

- 解锁只作用于**物理控制台会话**；远程桌面会话不处理。
- 锁屏与解锁都是状态收敛操作：重复下发不会产生第二次动作。
- 请求只入队，实际执行在监控循环的下一拍（约 1 秒内）；解锁最长可能等 20 秒（凭据提供程序超时）。
- 队列有界（16 条），进程重启后未执行请求会丢失，回查得到 `unknown_request`，重试安全。
- 已知限制：通道文件目录对 `BuiltinUsers` 可写（见架构分析 H1），因此在多用户机器上，
  本地低权限用户可绕过本接口直接写通道文件。签名鉴权只覆盖 HTTP 路径。
