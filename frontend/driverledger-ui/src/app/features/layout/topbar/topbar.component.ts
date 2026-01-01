import { Component, EventEmitter, Output } from '@angular/core';

@Component({
  selector: 'dl-topbar',
  standalone: true,
  templateUrl: './topbar.component.html',
})
export class TopbarComponent {
  @Output() ai = new EventEmitter<void>();
}
