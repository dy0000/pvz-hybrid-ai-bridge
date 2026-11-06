import json
import sys

from .client import PvzClient, BridgeError

if len(sys.argv) > 1 and sys.argv[1] == "mcp":
    from .mcp_server import main
    main()
else:
    try:
        with PvzClient() as client:
            print(json.dumps(client.observe(), ensure_ascii=False, indent=2))
    except BridgeError as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
