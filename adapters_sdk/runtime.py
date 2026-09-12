"""A small execution boundary for host-selected adapters.

Manifests cannot choose handlers, import Python, execute commands, or inject UI.
A trusted host explicitly registers each action with its shared scheduler. The
same class is usable by community hosts without depending on Atelier internals.
"""
from .manifest import AdapterManifest, ManifestError


class AdapterRegistry:
    def __init__(self):
        self._adapters = {}
        self._handlers = {}

    def register(self, manifest, handlers, *, trusted_origin='community'):
        manifest = manifest if isinstance(manifest, AdapterManifest) else AdapterManifest(manifest)
        data = manifest.data
        if trusted_origin not in ('official', 'community'):
            raise ManifestError('The host must establish adapter origin.')
        if data['id'] in self._adapters:
            raise ManifestError('Adapter id is already registered.')
        declared = {action['id'] for action in data['actions']}
        if set(handlers) != declared or any(not callable(handler) for handler in handlers.values()):
            raise ManifestError('Every declared action requires an explicit host handler.')
        # Never trust an adapter's self-declared official badge.
        data = dict(data, origin=trusted_origin)
        self._adapters[data['id']] = data
        self._handlers[data['id']] = dict(handlers)
        return data

    def manifests(self):
        import copy
        return copy.deepcopy(list(self._adapters.values()))

    def get(self, adapter_id):
        import copy
        if adapter_id not in self._adapters:
            raise ManifestError('This integration is not registered by the host.')
        return copy.deepcopy(self._adapters[adapter_id])

    def execute(self, adapter_id, action_id, context, inputs):
        manifest = self.get(adapter_id)
        action = next((item for item in manifest['actions'] if item['id'] == action_id), None)
        if action is None:
            raise ManifestError('This action is not declared by the integration.')
        if not isinstance(inputs, dict) or inputs:
            # The current first-party controls take their exact target from the
            # workspace. Expand this through versioned schemas when adding forms.
            raise ManifestError('This SDK version supports actions without custom inputs.')
        return self._handlers[adapter_id][action_id](context, inputs)
