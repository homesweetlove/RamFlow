"""SYNTHETIC interval tests, not experimental results."""
import unittest
from research_paper_audit import restricted_seconds


class IntervalTests(unittest.TestCase):
    def test_state_crossing_phase_boundaries_is_clipped(self):
        self.assertEqual(restricted_seconds([(-1,True),(2,False),(4,True),(8,False)],0,10),6)

    def test_no_intervention_is_zero_and_end_boundary_adds_no_time(self):
        self.assertEqual(restricted_seconds([],0,10),0)
        self.assertEqual(restricted_seconds([(10,True)],0,10),0)

    def test_duplicate_state_does_not_double_count(self):
        self.assertEqual(restricted_seconds([(1,True),(2,True),(5,False)],0,10),4)


if __name__=='__main__':unittest.main()
