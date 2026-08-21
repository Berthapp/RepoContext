resource "aws_s3_bucket" "refund_audit" {
  bucket = "refund-audit"
}

variable "region" {
  default = "eu-central-1"
}
