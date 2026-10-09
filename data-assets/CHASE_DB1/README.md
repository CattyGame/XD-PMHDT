# CHASE_DB1 — Dataset dùng chung cho AURA

Archive: CHASEDB1.zip, giữ nguyên nội dung gốc.

Nguồn: Kingston University, phối hợp với
St. George’s, University of London.

Trang dataset:
https://researchinnovation.kingston.ac.uk/en/datasets/chasedb1-retinal-vessel-reference-dataset-4/

Giấy phép được trang chính thức ghi cho archive: CC BY 4.0.
https://creativecommons.org/licenses/by/4.0/

Công trình liên quan:
https://doi.org/10.1109/TBME.2012.2205687

Nội dung: 28 ảnh võng mạc và 56 mask từ hai người đánh dấu.

Tại thư mục gốc repository, giải nén bằng PowerShell:

    Expand-Archive -Path data-assets/CHASE_DB1/CHASEDB1.zip -DestinationPath datasets/CHASE_DB1/raw

Nếu đã có đủ ảnh trong raw thì không cần giải nén lại.

Ảnh giải nén và predictions được bỏ qua bởi Git.
Tài liệu: docs/data_cards/dataset_card.md
Manifest: docs/datasets/chase_db1/manifest.csv