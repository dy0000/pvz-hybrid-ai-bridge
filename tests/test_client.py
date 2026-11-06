import io
import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import unittest
from unittest import mock
import os

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))
from pvz_ai.client import PvzClient, BridgeError, default_config
from pvz_ai.mcp_server import handle, TOOLS


class FakeClient:
    def __init__(self):
        self.calls = []

    def request(self, op, **kwargs):
        self.calls.append((op, kwargs))
        return {"ok": op != "plant", "state": {"battle_id": "test"}}


class ProtocolTests(unittest.TestCase):
    def test_signed_conveyor_card_id_is_forwarded_without_rebinding(self):
        client = FakeClient()
        args = {"battle_id": "current-battle", "card_id": -17, "column": 3, "row": 2}
        handle({"id": 1, "method": "tools/call", "params": {"name": "pvz_plant", "arguments": args}}, client)
        self.assertEqual(client.calls, [("plant", args)])

    def test_live_metadata_rejects_forged_success_and_bounded_flow(self):
        client = FakeClient()
        args = {"intents": [{"id": "i", "content_id": "PlantSunflowerPea", "column": 1, "row": 3, "status": "committed"}]}
        request = {"id": 1, "method": "tools/call", "params": {"name": "pvz_stream_update", "arguments": args}}
        self.assertTrue(handle(request, client)["result"]["isError"])
        args["intents"][0]["status"] = "pending"
        self.assertFalse(handle(request, client)["result"]["isError"])
        args["ttl_ms"] = 30001
        self.assertTrue(handle(request, client)["result"]["isError"])
        request["params"] = {"name": "pvz_flow_start", "arguments": {"max_levels": 0}}
        self.assertTrue(handle(request, client)["result"]["isError"])
        self.assertEqual(len(client.calls), 1)

    def test_capture_is_mcp_image_not_base64_in_text(self):
        client = FakeClient()
        client.request = lambda *args, **kwargs: {"ok": True, "path": "frame.png", "png_base64": "aGVsbG8="}
        response = handle({"id": 1, "method": "tools/call", "params": {"name": "pvz_capture_frame", "arguments": {}}}, client)
        content = response["result"]["content"]
        self.assertEqual(content[1], {"type": "image", "data": "aGVsbG8=", "mimeType": "image/png"})
        self.assertNotIn("aGVsbG8=", content[0]["text"])

    def test_plant_plan_nested_validation_and_cancel_status(self):
        client = FakeClient()
        arguments = {"battle_id": "b", "plan_id": "p", "sun_budget": 500,
                     "jobs": [{"id": "j", "content_id": "PlantSunflowerPea", "cells": [{"column": 1, "row": 3}]}]}
        request = {"id": 1, "method": "tools/call", "params": {"name": "pvz_set_plant_plan", "arguments": arguments}}
        self.assertFalse(handle(request, client)["result"]["isError"])
        for bad in (True, 0, "1"):
            arguments["jobs"][0]["cells"][0]["row"] = bad
            self.assertTrue(handle(request, client)["result"]["isError"])
        arguments["jobs"][0]["cells"][0]["row"] = 3
        arguments["jobs"][0]["cheat"] = True
        self.assertTrue(handle(request, client)["result"]["isError"])
        self.assertEqual(len(client.calls), 1)
        for name, args in (("pvz_cancel_plant_plan", {"battle_id": "b", "plan_id": "p"}),
                           ("pvz_plant_plan_status", {"after_event_id": 10})):
            request["params"] = {"name": name, "arguments": args}
            self.assertFalse(handle(request, client)["result"]["isError"])

    def test_auto_collect_requires_explicit_boolean_and_current_battle(self):
        client = FakeClient()
        request = {"id": 1, "method": "tools/call", "params": {"name": "pvz_auto_collect", "arguments": {"battle_id": "battle", "enabled": True}}}
        self.assertFalse(handle(request, client)["result"]["isError"])
        self.assertEqual(client.calls, [("auto_collect", {"battle_id": "battle", "enabled": True})])
        for arguments in ({"battle_id": "battle"}, {"enabled": True}, {"battle_id": "battle", "enabled": 1}):
            request["params"]["arguments"] = arguments
            self.assertTrue(handle(request, client)["result"]["isError"])
        self.assertEqual(len(client.calls), 1)

    def test_default_client_refuses_original_or_misbound_save_directory(self):
        with tempfile.TemporaryDirectory() as directory, mock.patch.dict(os.environ, {"APPDATA": directory, "PVZ_AI_CONNECTION": ""}):
            path = default_config()
            path.parent.mkdir(parents=True)
            for instance, user_dir in (("original", str(path.parent.parent)), ("sandbox", str(Path(directory)/"other"))):
                path.write_text(json.dumps({"host":"127.0.0.1","port":47632,"token":"test","instance":instance,"user_data_dir":user_dir}), encoding="utf-8")
                with mock.patch("socket.create_connection") as connect:
                    with self.assertRaises(BridgeError):
                        PvzClient().connect()
                    connect.assert_not_called()

    def test_mcp_default_realtime_and_mode_validation(self):
        client = FakeClient()
        request = {"id":1,"method":"tools/call","params":{"name":"pvz_take_control","arguments":{}}}
        self.assertFalse(handle(request,client)["result"]["isError"])
        self.assertEqual(client.calls, [("attach",{})])
        request["params"]["arguments"] = {"mode":"invalid"}
        self.assertTrue(handle(request,client)["result"]["isError"])
        self.assertEqual(len(client.calls),1)

    def test_mcp_handshake_and_catalog_do_not_connect_to_game(self):
        client = FakeClient()
        init = handle({"id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05"}}, client)
        self.assertEqual(init["result"]["protocolVersion"], "2024-11-05")
        listing = handle({"id": 2, "method": "tools/list"}, client)
        self.assertEqual(len(listing["result"]["tools"]), len(TOOLS))
        self.assertFalse(client.calls)
        self.assertTrue(all("_op" not in t for t in listing["result"]["tools"]))

    def test_mcp_rejects_invalid_coordinates_without_dispatch(self):
        client = FakeClient()
        for column in (0, -1, True, "3"):
            response = handle({"id": 1, "method": "tools/call", "params": {"name": "pvz_plant", "arguments": {"battle_id": "b", "card_id": 1, "column": column, "row": 1}}}, client)
            self.assertTrue(response["result"]["isError"])
        self.assertFalse(client.calls)

    def test_game_rejection_is_tool_error_not_success(self):
        client = FakeClient()
        response = handle({"id": 1, "method": "tools/call", "params": {"name": "pvz_plant", "arguments": {"battle_id": "b", "card_id": 1, "column": 2, "row": 1}}}, client)
        self.assertTrue(response["result"]["isError"])
        self.assertEqual(len(client.calls), 1)

    def test_notifications_have_no_response(self):
        self.assertIsNone(handle({"method": "notifications/initialized"}, FakeClient()))

    def test_transport_round_trip_and_no_retry_on_lost_response(self):
        for drop in (False, True):
            with self.subTest(drop=drop), tempfile.TemporaryDirectory() as directory:
                server = socket.socket()
                server.bind(("127.0.0.1", 0))
                server.listen(1)
                requests = []
                def serve():
                    with server:
                        conn, _ = server.accept()
                        with conn, conn.makefile("rb") as reader:
                            request = json.loads(reader.readline())
                            requests.append(request)
                            if not drop:
                                conn.sendall((json.dumps({"ok": True, "id": request["id"], "state": {"sun": 50}}) + "\n").encode())
                thread = threading.Thread(target=serve, daemon=True)
                thread.start()
                config = Path(directory) / "connection.json"
                config.write_text(json.dumps({"host": "127.0.0.1", "port": server.getsockname()[1], "token": "test"}), encoding="utf-8")
                with PvzClient(config, timeout=2) as client:
                    if drop:
                        with self.assertRaises(BridgeError):
                            client.plant("b", 3, 2, 1)
                    else:
                        self.assertEqual(client.observe()["state"]["sun"], 50)
                thread.join(timeout=2)
                self.assertEqual(len(requests), 1)
                self.assertEqual(requests[0]["token"], "test")

    def test_remote_endpoint_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / "connection.json"
            config.write_text('{"host":"example.com","port":80,"token":"x"}')
            with self.assertRaises(BridgeError):
                PvzClient(config).connect()


if __name__ == "__main__":
    unittest.main()
