namespace SpotifyGameRadio.Core.Speech;

/// Starter custom-vocabulary list seeded into a new RadioProfile, biasing
/// Whisper's transcription toward common milsim/tactical terms. Purely a
/// starting point — the user edits this freely in the Voice Chat UI.
public static class VoiceVocabularyDefaults
{
    public const string Starter =
        "FOB, RTB, LZ, HLZ, CASEVAC, MEDEVAC, WIA, KIA, RP, rally point, " +
        "danger close, contact front, resupply, rearm, AO, callsign, " +
        "fire team, overwatch, klick, grid, MGRS, azimuth, waypoint, " +
        "bearing, tower";
}
