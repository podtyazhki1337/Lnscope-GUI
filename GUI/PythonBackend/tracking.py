"""Manually seeded 2D tracking. No device interfaces or motor commands.

A fixed reference patch prevents unchecked template drift. Low similarity,
ambiguous matches and excessive displacement latch the track as lost.
Scores are image similarity values, not probabilities of biological identity.
"""
import math
import cv2
import numpy as np

PATCH_RADIUS = 15
SEARCH_RADIUS = 36
MIN_SCORE = 0.78
MIN_PEAK_MARGIN = 0.06
MIN_TEXTURE = 5.0


class PointTrack:
    def __init__(self, gray, x, y):
        if not all(math.isfinite(v) for v in (x, y)):
            raise ValueError("Select a finite image point.")
        self.x, self.y = int(round(x)), int(round(y))
        r = PATCH_RADIUS
        h, w = gray.shape
        if not r <= self.x < w-r or not r <= self.y < h-r:
            raise ValueError("Select a point at least 15 pixels from the image edge.")
        self.template = gray[self.y-r:self.y+r+1, self.x-r:self.x+r+1].copy()
        if float(self.template.std()) < MIN_TEXTURE:
            raise ValueError("This region has too little texture. Select a visible edge or feature.")
        self.score, self.margin = 1.0, 1.0
        self.lost, self.reason = False, "Selected manually"

    def update(self, gray):
        if self.lost:
            return
        h, w = gray.shape
        r, s = PATCH_RADIUS, SEARCH_RADIUS
        left, top = max(0, self.x-r-s), max(0, self.y-r-s)
        right, bottom = min(w, self.x+r+s+1), min(h, self.y+r+s+1)
        scores = cv2.matchTemplate(gray[top:bottom, left:right], self.template, cv2.TM_CCOEFF_NORMED)
        _, best, _, point = cv2.minMaxLoc(scores)
        other = scores.copy()
        px, py = point
        other[max(0, py-r):py+r+1, max(0, px-r):px+r+1] = -1
        second = float(other.max())
        self.score, self.margin = float(best), float(best-second)
        nx, ny = left+px+r, top+py+r
        if best < MIN_SCORE:
            self.reason = "Appearance changed / occluded / out of focus"
        elif self.margin < MIN_PEAK_MARGIN:
            self.reason = "Ambiguous match; select the point again"
        elif math.hypot(nx-self.x, ny-self.y) > SEARCH_RADIUS-2:
            self.reason = "Displacement exceeds the local search range"
        else:
            self.x, self.y = nx, ny
            self.reason = "Tracking"
            return
        self.lost = True

    def result(self):
        return dict(x=self.x, y=self.y, score=self.score, margin=self.margin,
                    lost=self.lost, reason=self.reason)
