# 架构

```text
AI 客户端
  │ stdio JSON-RPC / MCP
python/pvz_ai/mcp_server.py
  │ 一个持久 PvzClient，本机 TCP + 每次游戏加载生成的 token
127.0.0.1:47632
  │ 原生游戏主线程派发，当前 battle/card/menu/entity ID
src/PvzAiBridge/Entry.cs (.pmod)
  │ 游戏 Mod API 与原生操作
杂交重制版 0.31.0 C# 隔离副本
```

游戏引擎是 Godot/C#，与 Unity IL2CPP 恐怖版的 Frida 接入方式不同。玩家使用已有桥接包；开发构建在本机引用游戏和 Godot 的程序集，不把这些库复制进 .pmod。

默认实时模式不暂停等模型思考。观察支持完整、compact 和 delta；动作返回原生结果及当前观察。`Committed` 才确认种植提交，能力触发和受理请求不能等同击杀或已完成的场景转换。

每个安装位置有自己的新存档和连接文件；默认客户端检查 loopback、sandbox 实例及实际存档目录。显式 `PVZ_AI_CONNECTION` 可指定自建兼容环境，使用者负责绑定正确目标。控制只允许一个连接；旁观 overlay 读取 capabilities 返回的状态文件，不另连 TCP。

连接/认证、主线程调度、玩家停止、未知结果不重放、购买收据和原生选择释放由桥接负责。策略与授权在模型和调用方保持；不把种植、购入受理或回包外层 ok 当成最终效果。
