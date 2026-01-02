cd ../
pandoc help.md -o help.pdf \
  --template PDF/Eisvogel-3.3.0/eisvogel \
  --pdf-engine=xelatex \
  --listings \
  -V mainfont="DejaVu Sans"