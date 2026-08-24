// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Instrument configuration for the synthetic fixture: what says which analyzer measured.

namespace MARS.Test;

public static partial class SyntheticMzML
{
    /// <summary>Which instrument configuration list the fixture should carry.</summary>
    public enum MassAnalyzerLayout
    {
        /// <summary>No instrumentConfigurationList at all, as the fixture has always been.</summary>
        None,

        /// <summary>One configuration, a linear ion trap. The shape of a Stellar file.</summary>
        UnitResolutionTrap,

        /// <summary>
        /// Two configurations: an orbitrap named as the run default because it takes the MS1
        /// survey, and an Astral analyzer that only the MS2 spectra point at. Classifying this
        /// file by its run default gives the wrong answer, which is what makes it worth a
        /// fixture - it is the shape of a real Orbitrap Astral file.
        /// </summary>
        HybridOrbitrapAstral,
    }

    /// <summary>
    /// The instrumentConfigurationList element, or nothing. Each case opens with a newline
    /// because the placeholder it replaces sits at the end of the preceding line - so the
    /// "no configuration" case substitutes to nothing at all and leaves the fixture exactly
    /// as it was before this parameter existed.
    /// </summary>
    internal static string InstrumentConfiguration(MassAnalyzerLayout layout) => layout switch
    {
        MassAnalyzerLayout.UnitResolutionTrap => "\n" + """
                <instrumentConfigurationList count="1">
                  <instrumentConfiguration id="IC1">
                    <componentList count="2">
                      <source order="1"/>
                      <analyzer order="2">
                        <cvParam cvRef="MS" accession="MS:1000083" name="radial ejection linear ion trap" value=""/>
                      </analyzer>
                    </componentList>
                  </instrumentConfiguration>
                </instrumentConfigurationList>
            """,

        MassAnalyzerLayout.HybridOrbitrapAstral => "\n" + """
                <instrumentConfigurationList count="2">
                  <instrumentConfiguration id="IC1">
                    <componentList count="3">
                      <source order="1"/>
                      <analyzer order="2">
                        <cvParam cvRef="MS" accession="MS:1000081" name="quadrupole" value=""/>
                      </analyzer>
                      <analyzer order="3">
                        <cvParam cvRef="MS" accession="MS:1000484" name="orbitrap" value=""/>
                      </analyzer>
                    </componentList>
                  </instrumentConfiguration>
                  <instrumentConfiguration id="IC2">
                    <componentList count="3">
                      <source order="1"/>
                      <analyzer order="2">
                        <cvParam cvRef="MS" accession="MS:1000081" name="quadrupole" value=""/>
                      </analyzer>
                      <analyzer order="3">
                        <cvParam cvRef="MS" accession="MS:1003379" name="asymmetric track lossless time-of-flight analyzer" value=""/>
                      </analyzer>
                    </componentList>
                  </instrumentConfiguration>
                </instrumentConfigurationList>
            """,

        _ => string.Empty,
    };

    /// <summary>
    /// The run element's defaultInstrumentConfigurationRef attribute. On the hybrid layout this
    /// deliberately names IC1, the orbitrap, because that is what a real file does and what
    /// makes reading only the run default the wrong way to classify one.
    /// </summary>
    internal static string DefaultConfigurationAttribute(MassAnalyzerLayout layout) =>
        layout == MassAnalyzerLayout.None ? string.Empty : " defaultInstrumentConfigurationRef=\"IC1\"";

    /// <summary>
    /// The instrumentConfigurationRef an MS2 spectrum carries. Only the hybrid layout needs
    /// one; with a single configuration every spectrum inherits the run default.
    /// </summary>
    internal static string Ms2ConfigurationReference(MassAnalyzerLayout layout, int msLevel) =>
        layout == MassAnalyzerLayout.HybridOrbitrapAstral && msLevel == 2
            ? " instrumentConfigurationRef=\"IC2\""
            : string.Empty;
}
