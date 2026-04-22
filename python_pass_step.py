import opentap
import time
from OpenTap import Display, Verdict

class PythonPassStep(opentap.TestStep):
    __clr_attribute__ = [Display("Python Pass Step", "A simple Python step that passes.", "TapX")]
    __namespace__ = "python_test_steps"
    def __init__(self):
        super().__init__()
        self.Name = "Python Pass Step"
    def Run(self):
        self.log.Info("Python step running...")
        time.sleep(0.1)
        self.UpgradeVerdict(Verdict.Pass)
        self.log.Info("Python step done.")
