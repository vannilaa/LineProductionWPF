CUSTOMER LOGOS
==============
Put each logo in this folder (LineProductionWPF\Logos), named exactly as below.
.ico (best: includes a large size, 128 or 256 px) or .png both work; .ico is tried first.
The folder is copied next to the .exe on build/publish. A logo that is missing is simply not shown.
Rebuild/republish after adding files, or copy them into <exe folder>\Logos by hand.

Name rule: customer name from the product table, upper-case, without leading "PT." and trailing "TBK.",
every run of other characters -> "-", then lower-case.

CUSTOMER (product.customer)                 FILE
------------------------------------------------------------------------------------
PT. ASTRA DAIHATSU MOTOR                    astra-daihatsu-motor.ico
PT. ISUZU ASTRA MOTOR INDONESIA             isuzu-astra-motor-indonesia.ico
PT. FUJISEAT INDONESIA                      fujiseat-indonesia.ico
PT. TOYOTA MOTOR MANUFACTURING INDONESIA    toyota-motor-manufacturing-indonesia.ico
PT. KRAMAYUDHA TIGA BERLIAN                 kramayudha-tiga-berlian.ico
PT. RESTU IBU                               restu-ibu.ico
PT. PINDAD                                  pindad.ico
PT. FUJISEAT INDONESIA / TBINA              fujiseat-indonesia-tbina.ico
PT. TOYOTA TSUSHO INDONESIA                 toyota-tsusho-indonesia.ico
PT. DAIMLER CVMI                            daimler-cvmi.ico
PT. MITSUBISHI MOTORS KRAMAYUDHA INDONESIA  mitsubishi-motors-kramayudha-indonesia.ico
PT. HINO MOTOR MANUFACTURING IND.           hino-motor-manufacturing-ind.ico
PT. TOYOTA-ASTRA MOTOR                      toyota-astra-motor.ico
PT. MINDA ASEAN AUTOMOTIVE                  minda-asean-automotive.ico
PT. TRANSKON JAYA TBK.                      transkon-jaya.ico
PT. ASKI                                    aski.ico
PT. MEKAR ARMADA JAYA                       mekar-armada-jaya.ico
PT. JOKINDO                                 jokindo.ico
SUMITOMO                                    sumitomo.ico

"PT. FUJISEAT INDONESIA / TBINA" uses fujiseat-indonesia-tbina.ico if it exists, otherwise fujiseat-indonesia.ico.
A new customer added to the product table later: apply the name rule above.
