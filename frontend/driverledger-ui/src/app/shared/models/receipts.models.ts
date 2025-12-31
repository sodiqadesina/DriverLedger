export type ReceiptStatus =
  | 'Draft'
  | 'Submitted'
  | 'Processing'
  | 'Hold'
  | 'ReadyForPosting'
  | 'Posted'
  | 'Failed';

export type ReceiptListItemDto = {
  id: string;
  status: ReceiptStatus;
  fileObjectId: string;
  createdAt: string;
};

export type CreateReceiptRequestDto = { fileObjectId: string };
export type CreateReceiptResponseDto = { receiptId: string; status: ReceiptStatus };

export type SubmitReceiptResponseDto = { receiptId: string; status: ReceiptStatus };

export type ResolveHoldRequestDto = {
  resolutionJson: string;
  resubmit: boolean;
};

export type ResolveHoldResponseDto = {
  receiptId: string;
  receiptStatus: ReceiptStatus;
  resolvedBy: string;
};
