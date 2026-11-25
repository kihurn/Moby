package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PresenceLightingTest {
    @Test fun darknessAndUniformCoverCannotConfirmAbsence() {
        assertFalse(PresenceLighting.usable(IntArray(160 * 120) { 24 }, 160, 120))
        assertFalse(PresenceLighting.usable(IntArray(160 * 120) { 100 }, 160, 120))
        assertFalse(PresenceLighting.usable(IntArray(160 * 120) { 242 }, 160, 120))
    }

    @Test fun aWellLitVariedSceneCanSupplyEvidence() {
        assertTrue(PresenceLighting.usable(IntArray(160 * 120) { 72 + (it % 160) % 40 }, 160, 120))
    }

    @Test fun coveringHalfTheLensCannotConfirmAbsence() {
        assertFalse(PresenceLighting.usable(IntArray(160 * 120) {
            if (it % 160 < 80) 30 else 75 + (it % 160) % 45
        }, 160, 120))
    }
}
