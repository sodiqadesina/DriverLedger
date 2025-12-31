import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';

type ReceiptCard = {
  merchant: string;
  date: string;
  amount: string;
  status: 'Posted'|'Hold'|'Review';
  category: string;
  confidence: number;
};

@Component({
  selector: 'dl-receipts-page',
  standalone: true,
  imports: [CommonModule,FormsModule],
  templateUrl: './receipts.page.html',
})
export class ReceiptsPage implements OnInit {
  q = '';
  tab: 'all'|'review'|'posted' = 'all';

  cards: ReceiptCard[] = [
    { merchant:'Shell Gas Station', date:'2025-01-15', amount:'$65.42', status:'Posted', category:'Fuel', confidence:95 },
    { merchant:'Auto Parts Plus', date:'2025-01-14', amount:'$142.87', status:'Hold', category:'Maintenance', confidence:65 },
    { merchant:'Canadian Tire', date:'2025-01-12', amount:'$89.99', status:'Review', category:'', confidence:40 },
    { merchant:'Petro-Canada', date:'2025-01-10', amount:'$72.15', status:'Posted', category:'Fuel', confidence:98 },
    { merchant:'Quick Wash', date:'2025-01-08', amount:'$25.00', status:'Posted', category:'Car Wash', confidence:92 },
  ];

  ngOnInit(): void {}

  get filtered() {
    const text = this.q.trim().toLowerCase();
    return this.cards.filter(c => {
      const matchText = !text || c.merchant.toLowerCase().includes(text);
      const matchTab =
        this.tab === 'all' ||
        (this.tab === 'review' && (c.status === 'Hold' || c.status === 'Review')) ||
        (this.tab === 'posted' && c.status === 'Posted');
      return matchText && matchTab;
    });
  }

  pillClass(s: ReceiptCard['status']) {
    if (s === 'Posted') return 'bg-green-100 text-green-700';
    if (s === 'Hold') return 'bg-amber-100 text-amber-700';
    return 'bg-red-100 text-red-700';
  }
}
