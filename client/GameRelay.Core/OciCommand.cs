namespace GameRelay.Core;

/// <summary>
/// Generates a ready-to-paste Oracle Cloud Shell script that opens the relay
/// ports on the VCN's default Security List — the one firewall layer the app
/// can't reach over SSH. The user pastes it into Cloud Shell (which already has
/// the OCI CLI authenticated), avoiding the multi-click console form and any
/// local OCI API setup.
/// </summary>
public static class OciCommand
{
    /// <summary>
    /// Builds the Cloud Shell script. It finds the first VCN in the root
    /// compartment, takes its default security list, and appends idempotent
    /// ingress rules for TCP+UDP <paramref name="fromPort"/>–<paramref name="toPort"/>.
    /// </summary>
    public static string BuildSecurityListScript(int fromPort = 1024, int toPort = 65535)
    {
        // Raw (non-interpolated) so the JSON braces survive; ports substituted after.
        const string template = """
# GameRelay — open relay ports on your VCN's default Security List.
# Paste this whole block into Oracle Cloud Shell (>_ icon, top-right of the console).
set -euo pipefail
COMP="${OCI_TENANCY:?Run this inside Oracle Cloud Shell.}"
VCN=$(oci network vcn list -c "$COMP" --query 'data[0].id' --raw-output)
[ -n "$VCN" ] || { echo "No VCN found in the root compartment — create one first."; exit 1; }
SL=$(oci network vcn get --vcn-id "$VCN" --query 'data."default-security-list-id"' --raw-output)
oci network security-list get --security-list-id "$SL" --query 'data."ingress-security-rules"' --raw-output \
  | jq '. + [
      {"source":"0.0.0.0/0","protocol":"6","is-stateless":false,"tcp-options":{"destination-port-range":{"max":__TO__,"min":__FROM__}}},
      {"source":"0.0.0.0/0","protocol":"17","is-stateless":false,"udp-options":{"destination-port-range":{"max":__TO__,"min":__FROM__}}}
    ]' > /tmp/gr-ingress.json
oci network security-list update --security-list-id "$SL" \
  --ingress-security-rules file:///tmp/gr-ingress.json --force >/dev/null
echo "GameRelay: opened TCP + UDP __FROM__-__TO__ on VCN $VCN."
""";
        return template
            .Replace("__FROM__", fromPort.ToString())
            .Replace("__TO__", toPort.ToString());
    }
}
