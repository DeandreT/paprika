#!/bin/sh
# Run in the final Ubuntu image after its last apt transaction.
set -eu
export LC_ALL=C
dest=/app/notices
mkdir -p "$dest/os/copyright" "$dest/dotnet"
dpkg-query -W -f='${db:Status-Status}\t${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' \
    | awk -F '\t' '$1=="installed" {print $2 "\t" $3 "\t" $4 "\t" $5}' \
    | sort > "$dest/os/packages.tsv"
test -s "$dest/os/packages.tsv"
: > "$dest/os/missing-copyright.txt"
cut -f1 "$dest/os/packages.tsv" | while IFS= read -r name; do
    doc="/usr/share/doc/${name%%:*}/copyright"
    if [ -s "$doc" ]; then
        cp -L "$doc" "$dest/os/copyright/$name.txt"
    else
        echo "$name" >> "$dest/os/missing-copyright.txt"
    fi
done
cp -rL /usr/share/common-licenses "$dest/os/"
for name in fonts-dejavu-core libfontconfig1; do
    test -s "/usr/share/doc/$name/copyright"
done
cp -L /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt "$dest/dotnet/"
dotnet --list-runtimes > "$dest/dotnet/runtimes.txt"
