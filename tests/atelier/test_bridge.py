import json
import unittest
from unittest.mock import Mock
from atelier.bridge import BridgeClient, BridgeError
class BridgeTests(unittest.TestCase):
    def test_command_shape(self):
        c=BridgeClient('http://127.0.0.1:1','token'); self.assertEqual(c.protocol_version,'1')

    def test_submit_preserves_nested_target_and_uses_authenticated_protocol(self):
        client = BridgeClient('http://127.0.0.1:1234', 'secret')
        response = Mock()
        response.read.return_value = b'{"state":"succeeded"}'
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        client._opener.open = Mock(return_value=response)
        result = client.submit({'id': '123e4567-e89b-12d3-a456-426614174000', 'workspaceId': '123e4567-e89b-12d3-a456-426614174001', 'target': {'sceneGuid': 'scene', 'objectId': 'GlobalObjectId'}, 'action': 'snapshot', 'payload': {'view': 'front'}})
        self.assertEqual(result['state'], 'succeeded')
        request = client._opener.open.call_args.args[0]
        self.assertEqual(request.get_header('Authorization'), 'Bearer secret')
        self.assertEqual(request.get_header('X-atelier-protocol'), '1')
        self.assertEqual(json.loads(request.data)['target']['objectId'], 'GlobalObjectId')

    def test_rejects_non_loopback_and_oversize_payloads(self):
        with self.assertRaises(ValueError): BridgeClient('http://example.com:80', 'token')
        client = BridgeClient('http://127.0.0.1:1', 'token')
        with self.assertRaises(BridgeError): client.submit({'target': {}, 'payload': {'text': 'x' * (300 * 1024)}})
