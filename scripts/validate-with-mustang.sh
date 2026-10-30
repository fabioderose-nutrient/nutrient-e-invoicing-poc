#!/usr/bin/env bash
# Independent check of the generated invoices with the open-source Mustang validator
# (https://www.mustangproject.org, Apache License 2.0). Mustang validates the PDF/A-3 part
# with veraPDF and the XML with the official EN 16931 / Factur-X schematron rules.
#
# Usage: scripts/validate-with-mustang.sh [file.pdf ...]   (default: output/*.pdf samples/incoming/*.pdf)
# Needs Java 17 or later and curl. The jar is downloaded once to tools/ (not committed).
set -euo pipefail

VERSION="2.26.0"
JAR="tools/Mustang-CLI-${VERSION}.jar"
URL="https://repo1.maven.org/maven2/org/mustangproject/Mustang-CLI/${VERSION}/Mustang-CLI-${VERSION}.jar"

cd "$(dirname "$0")/.."
mkdir -p tools output/validation

if [ ! -f "$JAR" ]; then
  echo "Downloading Mustang-CLI ${VERSION} from Maven Central ..."
  curl -fsSL -o "$JAR.part" "$URL"
  expected="$(curl -fsSL "$URL.sha1" | cut -c1-40)"
  if command -v sha1sum >/dev/null; then actual="$(sha1sum "$JAR.part" | cut -c1-40)"; else actual="$(shasum -a 1 "$JAR.part" | cut -c1-40)"; fi
  if [ "$expected" != "$actual" ]; then
    echo "Checksum mismatch for $JAR (expected $expected, got $actual)." >&2
    rm -f "$JAR.part"
    exit 1
  fi
  mv "$JAR.part" "$JAR"
fi

if [ "$#" -gt 0 ]; then files=("$@"); else files=(output/*.pdf samples/incoming/*.pdf); fi

printf '%-28s %-10s %-8s %-8s %s\n' "File" "PDF/A" "XML" "Overall" "Errors"
for f in "${files[@]}"; do
  [ -f "$f" ] || continue
  report="output/validation/$(basename "${f%.pdf}").mustang.xml"
  java -jar "$JAR" --action validate --source "$f" --disable-file-logging 2>/dev/null > "$report" || true
  pdf="$(sed -n '/<pdf>/,/<\/pdf>/p' "$report" | grep -o 'summary status="[a-z]*"' | head -1 | cut -d'"' -f2)"
  xml="$(sed -n '/<xml>/,/<\/xml>/p' "$report" | grep -o 'summary status="[a-z]*"' | head -1 | cut -d'"' -f2)"
  overall="$(grep -o 'summary status="[a-z]*"' "$report" | tail -1 | cut -d'"' -f2)"
  errors="$(grep '<error' "$report" | grep -o '\[BR-[A-Z0-9-]*\]' | sort -u | tr '\n' ' ' || true)"
  printf '%-28s %-10s %-8s %-8s %s\n' "$(basename "$f")" "${pdf:-?}" "${xml:-?}" "${overall:-?}" "$errors"
done
echo "Full reports: output/validation/"
