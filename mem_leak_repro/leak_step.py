# Trivial Python-derived TestStep — any python step triggers the leak.
# The point is the *kind* of object (Python-derived ClassDerived), not what it does.
import opentap
from opentap import *
import OpenTap
from OpenTap import Verdict


@attribute(OpenTap.Display("Leak Step", "Trivial python step used by mem_leak_repro.", "TapX"))
class LeakStep(TestStep):
    def __init__(self):
        super().__init__()
        self.Name = "Leak Step"

    def Run(self):
        self.UpgradeVerdict(Verdict.Pass)
